using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;

namespace MechVibe
{

    /// <summary>
    /// Exposes MechWarrior 5: Clans telemetry to SimHub as properties, so ShakeIt
    /// Bass Shakers can drive several transducers with different effects.
    ///
    /// Data comes from the "MechVibeMemory" shared memory published by
    /// MechVibeBridge, which in turn is fed by the UE4SS Lua mod. The
    /// mapping is read-only, so this plugin and MechVibe.exe can both consume the
    /// same feed at the same time.
    ///
    /// SimHub does not know MW5: Clans as a game, so DataUpdate may never fire.
    /// A background poller keeps the values current instead, and every property is
    /// registered through AttachDelegate, which SimHub evaluates on demand.
    /// </summary>
    [PluginName("MechWarrior 5: Clans Telemetry")]
    [PluginAuthor("MW5 Clans MechVibe port")]
    [PluginDescription("Weapon, movement and damage telemetry from MechWarrior 5: Clans, for driving bass shakers with ShakeIt")]
    public class MechVibe : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        public const string PluginVersion = "1.0.0";

        public PluginManager PluginManager { get; set; }

        public MW5ClansSettings Settings;

        private static readonly string[] Sides = { "Left", "Right", "Center" };

        /// <summary>
        /// EMechParts, indexed by its own value (MechWarrior_enums.hpp:1228).
        /// Used for per-part destruction channels.
        /// </summary>
        private static readonly string[] MechParts =
        {
            "Head", "CenterTorso", "LeftTorso", "LeftArm",
            "LeftLeg", "RightTorso", "RightArm", "RightLeg"
        };
        // Codes the Lua mod puts in Float4 of a Projectile event - must match
        // PROJ_* in main.lua exactly.
        private const int PROJ_PPC   = 0;
        private const int PROJ_AC    = 1;
        private const int PROJ_LBX   = 2;
        private const int PROJ_GAUSS = 3;
        private const int PROJ_UAC   = 4;
        private const int PROJ_RAC   = 5;

        /// <summary>
        /// Ballistic sub-types, indexed by the code above. "Projectile" is the plain
        /// autocannon and the fallback - it keeps that name so existing formulas
        /// referring to it still work.
        /// </summary>
        private static readonly string[] ProjectileTypes =
        {
            "PPC", "Projectile", "LBX", "Gauss", "UAC", "RAC"
        };

        private static string ProjectileTypeName(int subtype)
        {
            return subtype >= 0 && subtype < ProjectileTypes.Length
                 ? ProjectileTypes[subtype]
                 : "Projectile";
        }

        private static readonly string[] PulseWeaponTypes   = { "Laser", "PulseLaser", "Projectile", "PPC", "Missile", "LBX", "Gauss", "UAC", "RAC" };
        private static readonly string[] SustainWeaponTypes = { "MachineGun", "Flamer", "TAG", "AMS" };

        private const string SharedMemoryName = "MechVibeMemory";
        private const int    BufferSize       = 128;
        private const int    ControlBlockSize = 16;
        private const int    EventDataSize    = 32;
        private const int    PollIntervalMs   = 2;
        private const int    TwistTimeoutMs   = 120;

        private Thread            _pollThread;
        private volatile bool     _running;
        private volatile bool     _connected;
        private long              _packetsSeen;

        // ---- channels -------------------------------------------------------

        // Momentary events (a shot, a hit, a footfall) fade out over a short window.
        // Sustained ones (flamer held down, jump jets) stay put until switched off.
        private readonly Dictionary<string, Pulse>    _pulses    = new Dictionary<string, Pulse>();
        private readonly Dictionary<string, Sustain>  _sustains  = new Dictionary<string, Sustain>();
        private readonly Dictionary<string, Repeater> _repeaters = new Dictionary<string, Repeater>();
        private readonly Dictionary<string, Burst>    _bursts    = new Dictionary<string, Burst>();

        // Latest raw values, exposed alongside the normalised intensities so ShakeIt
        // formulas can work off the real numbers when the presets don't fit.
        private volatile float _mechTons;
        private volatile float _speedKmh;
        private volatile float _lastDamage;
        private volatile int   _lastDamageType = -1;
        private volatile int   _lastPartDestroyed = -1;
        private volatile int   _powerState = -1;
        private volatile float _torsoYaw;
        private volatile float _torsoPitch;
        // The torso feed is a level, not a trigger, so it needs a keep-alive: if
        // the mod stops sending (mission end, pause, crash) the last level would
        // otherwise hold forever. The mod sends every 50ms while turning.
        private volatile int   _lastTwistTick;
        private volatile int   _lastMissileCount;
        private volatile int   _lastMissileSalvo;
        private volatile float _impactForward;
        private volatile float _impactRight;

        private Pulse P(string name)
        {
            Pulse p;
            if (!_pulses.TryGetValue(name, out p))
            {
                p = new Pulse();
                _pulses[name] = p;
            }
            return p;
        }

        private Sustain S(string name)
        {
            Sustain s;
            if (!_sustains.TryGetValue(name, out s))
            {
                s = new Sustain();
                _sustains[name] = s;
            }
            return s;
        }

        private Burst B(string name)
        {
            Burst b;
            if (!_bursts.TryGetValue(name, out b))
            {
                b = new Burst();
                _bursts[name] = b;
            }
            return b;
        }

        /// <summary>
        /// A layer channel that can be driven either as a single impulse or as a
        /// burst; whichever fired last is the one that is audible.
        /// </summary>
        private void AddLayer(string name)
        {
            Pulse p = P(name);
            Burst b = B(name);
            this.AttachDelegate(name, () => Math.Max(p.Value, b.Value) * ShakeItScale);
        }

        private Repeater R(string name)
        {
            Repeater r;
            if (!_repeaters.TryGetValue(name, out r))
            {
                r = new Repeater();
                _repeaters[name] = r;
            }
            return r;
        }

        /// <summary>
        /// Drives a continuous weapon channel: pulsing per shot when a rate of fire
        /// is known and pulsing is enabled, otherwise holding a steady level.
        /// A flamer has no rate of fire, so it stays solid either way.
        /// </summary>
        private void SetContinuous(string channel, bool active, float level, float rateOfFire)
        {
            bool pulsed = Settings.RepeatPulseEnabled >= 0.5 && rateOfFire > 0f;

            if (!active)
            {
                R(channel).Stop();
                S(channel).Set(0f);
                return;
            }

            if (pulsed)
            {
                R(channel).Start(level, rateOfFire, Settings.RepeatPulseWidth, Settings.RepeatMaxRate);
                S(channel).Set(0f);
            }
            else
            {
                R(channel).Stop();
                S(channel).Set(level);
            }
        }

        // ---- plugin lifecycle -----------------------------------------------

        public void Init(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("[MW5Clans] plugin starting (v" + PluginVersion + ")");

            Settings = this.ReadCommonSettings<MW5ClansSettings>("GeneralSettings", () => new MW5ClansSettings());

            // Weapons fired by the player.
            // Layers, not plain pulses: these can fire either as one hit or as a
            // burst (pulse laser, UAC, missile stream), and AddLayer surfaces both.
            AddLayer("Weapon.Laser");
            AddLayer("Weapon.PulseLaser");
            AddLayer("Weapon.Projectile");   // plain AC / rifle
            AddLayer("Weapon.LBX");          // cluster
            AddLayer("Weapon.Gauss");
            AddLayer("Weapon.UAC");
            AddLayer("Weapon.RAC");
            AddLayer("Weapon.PPC");
            AddLayer("Weapon.Missile");
            AddPulse("Weapon.Melee");
            AddSustain("Weapon.MachineGun");
            AddSustain("Weapon.Flamer");
            AddSustain("Weapon.TAG");
            AddSustain("Weapon.AMS");

            // Movement and chassis.
            AddPulse("Move.Footstep");
            AddPulse("Move.Footstep.Left");
            AddPulse("Move.Footstep.Right");
            AddPulse("Move.Landed");
            AddSustain("Move.JumpJets");
            AddSustain("Move.Airborne");
            AddSustain("Move.MASC");
            AddSustain("Move.TorsoTwist");

            // Damage taken, split by the protocol's damage categories.
            AddPulse("Damage.Trace");
            AddPulse("Damage.Projectile");
            AddPulse("Damage.Missile");
            AddPulse("Damage.Melee");
            AddPulse("Damage.Explosion");
            AddPulse("Damage.Any");
            AddPulse("Damage.PartDestruction");
            AddPulse("Damage.PartDestruction.Left");
            AddPulse("Damage.PartDestruction.Right");
            AddPulse("Damage.PartDestruction.Center");

            // Per-part destruction. Losing an arm and losing a leg are different
            // events worth feeling differently, and destruction is rare enough that
            // eight channels cost nothing.
            foreach (string part in MechParts)
                AddPulse("Damage.PartDestruction." + part);

            // Firing position. The Lua mod encodes the hardpoint into WeaponId as
            // MechPart*1000 + SlotId, so every weapon event carries which side of
            // the mech it came from - enough to give shots a left/right feel.
            AddPulse("Weapon.Left");
            AddPulse("Weapon.Right");
            AddPulse("Weapon.Center");

            // Type x side. Needed because the two axes above can't be combined in a
            // formula: firing a left missile and a right laser at once lights up both
            // Weapon.Laser and Weapon.Left, which multiplied would read as "left
            // laser". These channels only fire when that exact weapon on that exact
            // side goes off, so a channel can carry "left laser" at its own gain.
            foreach (string side in Sides)
            {
                foreach (string type in PulseWeaponTypes)
                    AddLayer("Weapon." + side + "." + type);
                foreach (string type in SustainWeaponTypes)
                    AddSustain("Weapon." + side + "." + type);

                // The PPC discharge specifically. A PPC is two layers in the
                // original - a low pop and a high ringing tail - and only the pop
                // is an impact, so only the pop is worth placing left or right.
                AddPulse("Weapon." + side + ".PPC.Pop");

                // Same split for missiles: the launch impulse is an impact and
                // worth a side; the tail is ringing with no direction, so it
                // stays on the combined channel only (mirrors PPC.Pop/Tail above).
                AddLayer("Weapon." + side + ".Missile.Launch");

                // Melee isn't in PulseWeaponTypes above (it's not a ranged weapon
                // type), so both its halves need their own side channels here -
                // unlike PPC/Missile, both the swing and the connect are physical
                // motion with a real arm behind them.
                AddPulse("Weapon." + side + ".Melee.Hit");
                AddPulse("Weapon." + side + ".Melee.Swing");
            }

            // Two-layer effects. Each layer is its own channel so ShakeIt can give the
            // sharp front and the long tail different frequencies - that contrast is
            // the whole point of the pairing in the original.
            AddLayer("Weapon.Missile.Launch");
            AddLayer("Weapon.Missile.Tail");
            AddLayer("Weapon.PPC.Pop");
            AddLayer("Weapon.PPC.Tail");
            AddPulse("Weapon.Melee.Hit");
            AddPulse("Weapon.Melee.Swing");
            AddPulse("Damage.PartDestruction.Primary");
            AddPulse("Damage.PartDestruction.Secondary");

            // Physical shock, by the direction it came from. The Lua mod projects the
            // game's impulse vector onto the mech's own axes, so these are relative to
            // where the mech is facing - not world directions.
            AddPulse("Impact.Any");
            AddPulse("Impact.Front");
            AddPulse("Impact.Right");
            AddPulse("Impact.Rear");
            AddPulse("Impact.Left");
            AddPulse("Impact.Above");
            AddPulse("Impact.Below");

            // Sequences. Powering is split by stage - the original gives each a very
            // different duration (5s / 4s / 3.3s).
            AddPulse("Event.Powering");
            AddPulse("Event.Powering.Init");
            AddPulse("Event.Powering.Up");
            AddPulse("Event.Powering.Down");
            AddPulse("Event.Dropship");

            // Raw/state values.
            this.AttachDelegate("Connected", () => _connected);
            this.AttachDelegate("PacketsSeen", () => _packetsSeen);
            this.AttachDelegate("MechTons", () => _mechTons);
            this.AttachDelegate("SpeedKmh", () => _speedKmh);
            this.AttachDelegate("LastDamage", () => _lastDamage);
            this.AttachDelegate("LastDamageType", () => _lastDamageType);
            this.AttachDelegate("LastPartDestroyed", () => _lastPartDestroyed);
            this.AttachDelegate("PowerState", () => _powerState);

            // Torso angles, so a ShakeIt formula can bias left/right channels by
            // which way the torso is facing.
            this.AttachDelegate("TorsoYaw", () => _torsoYaw);
            this.AttachDelegate("TorsoPitch", () => _torsoPitch);

            // Last shock direction as raw components, for formulas that want to blend
            // rather than pick a channel.
            // Missile salvo shape - how many went out at once, and whether they` +
            // left together (salvo) or one after another (stream).
            this.AttachDelegate("LastMissileCount", () => _lastMissileCount);
            this.AttachDelegate("LastMissileSalvo", () => _lastMissileSalvo);

            this.AttachDelegate("ImpactForward", () => _impactForward);
            this.AttachDelegate("ImpactRight", () => _impactRight);

            // SimHub may expose properties under a prefix of its own, and a ShakeIt
            // formula has to use the exact registered name. Log a few so the real
            // form is visible rather than guessed at.
            try
            {
                int shown = 0;
                foreach (string n in pluginManager.GetAllPropertiesNames())
                {
                    if (n != null && n.IndexOf("MW5Clans", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        SimHub.Logging.Current.Info("[MW5Clans] registered property: " + n);
                        if (++shown >= 5) break;
                    }
                }
                if (shown == 0)
                    SimHub.Logging.Current.Info("[MW5Clans] no property containing 'MW5Clans' found in the registry");
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[MW5Clans] property enumeration failed: " + ex.Message);
            }

            _running    = true;
            _pollThread = new Thread(PollLoop);
            _pollThread.IsBackground = true;
            _pollThread.Name = "MechVibePoller";
            _pollThread.Start();
        }

        /// <summary>
        /// ShakeIt formulas are expected to return 0-100, not 0-1 ("수식은 0에서 100
        /// 사이의 값을 반환해야 합니다" in the effect editor). Intensities are computed
        /// as 0-1 internally and scaled only at the point they're published, so a
        /// formula can use a property directly without multiplying.
        /// </summary>
        private const float ShakeItScale = 100f;

        private void AddPulse(string name)
        {
            Pulse p = P(name);
            this.AttachDelegate(name, () => p.Value * ShakeItScale);
        }

        private void AddSustain(string name)
        {
            // A continuous channel is either held (flamer) or pulsed per shot
            // (machine gun); only one is ever non-zero, so max() picks the live one.
            Sustain  s = S(name);
            Repeater r = R(name);
            this.AttachDelegate(name, () => Math.Max(s.Value, r.Value) * ShakeItScale);
        }

        /// <summary>
        /// Intentionally empty. SimHub has no game reader for MW5: Clans, so this
        /// is not a reliable tick - the background poller owns all updates.
        /// </summary>
        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
        }

        // ---- settings page ---------------------------------------------------

        public string LeftMenuTitle { get { return "MW5: Clans"; } }

        /// <summary>
        /// No menu icon. SimHub falls back to a default rather than failing, and
        /// shipping an image would mean embedding a resource just for decoration.
        /// </summary>
        public ImageSource PictureIcon { get { return null; } }

        /// <summary>
        /// Built in code instead of XAML so the plugin still compiles with a bare
        /// csc.exe - no MSBuild XAML pass, no .NET SDK. The rows are generated from
        /// MW5ClansSettings' fields, so adding a setting there adds a slider here.
        /// </summary>
        public Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(new TextBlock
            {
                Text       = "Shake timing",
                FontSize   = 18,
                FontWeight = FontWeights.Bold,
                Margin     = new Thickness(0, 0, 0, 4)
            });

            root.Children.Add(new TextBlock
            {
                Text = "The shape of each shake over time. Defaults match MechVibe's own "
                     + "settings. Strength, frequency and channel routing are set per effect "
                     + "in ShakeIt - this page controls timing and weighting only.",
                TextWrapping = TextWrapping.Wrap,
                Opacity      = 0.7,
                Margin       = new Thickness(0, 0, 0, 16)
            });

            // Grouped by section, and within a section by dropdown: every envelope and
            // every slider files into some named group, so a weapon's envelope and the
            // sliders that tune it end up in the same dropdown instead of the sliders
            // sitting loose below every envelope in the section.
            FieldInfo[] fields = typeof(MW5ClansSettings).GetFields();

            foreach (Section section in (Section[])Enum.GetValues(typeof(Section)))
            {
                var groupOrder = new List<string>();
                var groupBody  = new Dictionary<string, StackPanel>();

                Func<string, StackPanel> bodyFor = label =>
                {
                    StackPanel panel;
                    if (!groupBody.TryGetValue(label, out panel))
                    {
                        panel = new StackPanel { Margin = new Thickness(8, 6, 0, 6) };
                        groupBody[label] = panel;
                        groupOrder.Add(label);
                    }
                    return panel;
                };

                // One pass in field-declaration order, so fields placed together in the
                // source end up together on screen regardless of which group absorbs them.
                foreach (FieldInfo field in fields)
                {
                    var g = (EffectGroupAttribute[])field.GetCustomAttributes(typeof(EffectGroupAttribute), false);
                    if (g.Length > 0 && g[0].Section == section)
                        AddEnvelopeRows(bodyFor(g[0].Label), field);

                    var s = (SettingAttribute[])field.GetCustomAttributes(typeof(SettingAttribute), false);
                    if (s.Length > 0 && s[0].Section == section)
                    {
                        FieldInfo f = field;
                        bodyFor(s[0].Group ?? s[0].Label).Children.Add(BuildSliderRow(
                            () => (double)f.GetValue(Settings),
                            v  => f.SetValue(Settings, v),
                            s[0].Label, s[0].Min, s[0].Max,
                            f.Name.EndsWith("Scale") ? "x" : ""));
                    }
                }

                if (groupOrder.Count == 0) continue;

                root.Children.Add(BuildSectionHeader(SectionTitle(section), SectionHint(section)));

                foreach (string label in groupOrder)
                {
                    root.Children.Add(new Expander
                    {
                        Header  = label,
                        Content = groupBody[label],
                        Margin  = new Thickness(0, 0, 0, 4)
                    });
                }
            }

            var reset = new Button
            {
                Content = "Reset to defaults",
                Margin  = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(12, 4, 12, 4),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            reset.Click += (s, e) =>
            {
                Settings = new MW5ClansSettings();
                this.SaveCommonSettings("GeneralSettings", Settings);
                // Rebuilding the page is the simplest way to show the new values.
                MessageBox.Show("Defaults restored. Reopen this page to see the sliders update.",
                                "MW5: Clans Telemetry");
            };
            root.Children.Add(reset);

            return new ScrollViewer
            {
                Content = root,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
        }

        /// <summary>
        /// One collapsible group of sliders for an EffectShape: the five numbers that
        /// decide how long a shake lasts and what shape it has.
        /// </summary>
        private static string SectionTitle(Section s)
        {
            switch (s)
            {
                case Section.Weapons:  return "Weapons fired";
                case Section.Movement: return "Movement and chassis";
                case Section.Damage:   return "Damage taken";
                case Section.Events:   return "Events";
                default:               return s.ToString();
            }
        }

        private static string SectionHint(Section s)
        {
            switch (s)
            {
                case Section.Weapons:  return "What your own weapons feel like when they fire.";
                case Section.Movement: return "Footfalls, landings, jets, torso rotation.";
                case Section.Damage:   return "Being hit, and losing parts.";
                case Section.Events:   return "Power up and down, dropship sequences.";
                default:               return "";
            }
        }

        private UIElement BuildSectionHeader(string title, string hint)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 20, 0, 8) };

            panel.Children.Add(new TextBlock
            {
                Text       = title,
                FontSize   = 17,
                FontWeight = FontWeights.Bold
            });

            if (hint.Length > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text         = hint,
                    Opacity      = 0.6,
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(0, 2, 0, 6)
                });
            }

            panel.Children.Add(new Border
            {
                Height     = 1,
                Opacity    = 0.25,
                Background = System.Windows.Media.Brushes.Gray,
                Margin     = new Thickness(0, 0, 0, 4)
            });

            return panel;
        }

        private void AddEnvelopeRows(StackPanel body, FieldInfo field)
        {
            var shape = (EffectShape)field.GetValue(Settings);

            body.Children.Add(BuildSliderRow(
                () => shape.MinLength, v => shape.MinLength = v,
                "Length at weakest", 0.0, 3.0, "s"));

            body.Children.Add(BuildSliderRow(
                () => shape.MaxLength, v => shape.MaxLength = v,
                "Length at strongest", 0.02, 3.0, "s"));

            body.Children.Add(BuildSliderRow(
                () => shape.AttackEnd, v => shape.AttackEnd = v,
                "Attack (ramp up)", 0.0, 1.0, "%"));

            body.Children.Add(BuildSliderRow(
                () => shape.DecayStart, v => shape.DecayStart = v,
                "Decay start", 0.0, 1.0, "%"));

            body.Children.Add(BuildSliderRow(
                () => shape.LengthExponent, v => shape.LengthExponent = v,
                "Length curve (lower = weak hits still long)", 0.05, 2.0, ""));

            body.Children.Add(BuildSliderRow(
                () => shape.AmplitudeExponent, v => shape.AmplitudeExponent = v,
                "Amplitude curve (lower = weak hits still loud)", 0.05, 2.0, ""));

            // Above 1 boosts weapons the game reports with a low impulse (PPC is the
            // usual case). The result is clamped, so raising this on something that
            // already peaks changes nothing.
            body.Children.Add(BuildSliderRow(
                () => shape.Amplitude, v => shape.Amplitude = v,
                "Strength", 0.0, 2.0, ""));
        }

        /// <summary>
        /// A labelled slider bound to a getter/setter pair rather than a field, so the
        /// same row works for both plain settings and EffectShape members.
        /// </summary>
        private UIElement BuildSliderRow(Func<double> get, Action<double> set,
                                         string label, double min, double max, string unit)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

            Func<double, string> format = v =>
                unit == "%" ? (v * 100).ToString("0") + " %"
                            : v.ToString("0.00") + (unit.Length > 0 ? " " + unit : "");

            var header = new DockPanel();
            var value  = new TextBlock
            {
                Text          = format(get()),
                FontWeight    = FontWeights.Bold,
                MinWidth      = 70,
                TextAlignment = TextAlignment.Right
            };
            DockPanel.SetDock(value, Dock.Right);
            header.Children.Add(value);
            header.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(header);

            var slider = new Slider
            {
                Minimum             = min,
                Maximum             = max,
                Value               = get(),
                TickFrequency       = 0.01,
                IsSnapToTickEnabled = true
            };
            slider.ValueChanged += (s, e) =>
            {
                set(e.NewValue);
                value.Text = format(e.NewValue);
                // Saved immediately: SimHub's plugin pages have no OK/Apply button,
                // and End() only runs at shutdown.
                this.SaveCommonSettings("GeneralSettings", Settings);
            };
            row.Children.Add(slider);

            return row;
        }

        public void End(PluginManager pluginManager)
        {
            _running = false;
            if (_pollThread != null && _pollThread.IsAlive)
                _pollThread.Join(500);
            SimHub.Logging.Current.Info("[MW5Clans] plugin stopped");
        }

        // ---- shared memory polling ------------------------------------------

        private void PollLoop()
        {
            MemoryMappedFile         mmf      = null;
            MemoryMappedViewAccessor accessor = null;
            ControlBlock             last     = new ControlBlock();

            while (_running)
            {
                try
                {
                    if (accessor == null)
                    {
                        try
                        {
                            mmf      = MemoryMappedFile.OpenExisting(SharedMemoryName, MemoryMappedFileRights.Read);
                            accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                            // Start from the current head so a backlog of stale events
                            // doesn't fire everything at once on connect.
                            accessor.Read(0, out last);
                            _connected = true;
                            SimHub.Logging.Current.Info("[MW5Clans] connected to MechVibeBridge");
                        }
                        catch (FileNotFoundException)
                        {
                            _connected = false;
                            Thread.Sleep(2000);
                            continue;
                        }
                    }

                    ControlBlock cur;
                    Thread.MemoryBarrier();
                    accessor.Read(0, out cur);
                    Thread.MemoryBarrier();

                    long newCount = cur.PacketNumber - last.PacketNumber;
                    if (newCount > 0)
                    {
                        // If we fell behind by more than the ring holds, the older
                        // entries are already overwritten - take what's still there.
                        if (newCount > BufferSize)
                            newCount = BufferSize;

                        // WriteIndex is the slot most recently written, so the oldest
                        // unread entry sits newCount-1 slots behind it.
                        long idx = ((cur.WriteIndex - newCount + 1) % BufferSize + BufferSize) % BufferSize;

                        for (long i = 0; i < newCount; i++)
                        {
                            EventData ev;
                            accessor.Read(ControlBlockSize + EventDataSize * idx, out ev);
                            idx = (idx + 1) % BufferSize;
                            Handle(ev);
                        }

                        _packetsSeen += newCount;
                    }

                    last = cur;

                    // Silence a torso level the mod stopped refreshing. This is the
                    // normal way a turn ends, not just a failure path: coming to a
                    // full stop means OnTorsoTwist stops firing, so the mod never
                    // gets to send its zero. The window is the mod's 50ms send
                    // interval plus room for a dropped frame - long enough not to
                    // stutter mid-turn, short enough not to be felt as a hangover.
                    int twistTick = _lastTwistTick;
                    if (twistTick != 0 && unchecked(Environment.TickCount - twistTick) > TwistTimeoutMs)
                    {
                        _lastTwistTick = 0;
                        S("Move.TorsoTwist").Set(0f);
                    }
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Info("[MW5Clans] poll error: " + ex.Message);
                    Disconnect(ref mmf, ref accessor);
                }

                Thread.Sleep(PollIntervalMs);
            }

            Disconnect(ref mmf, ref accessor);
        }

        private void Disconnect(ref MemoryMappedFile mmf, ref MemoryMappedViewAccessor accessor)
        {
            _connected = false;
            if (accessor != null) { accessor.Dispose(); accessor = null; }
            if (mmf != null) { mmf.Dispose(); mmf = null; }
            AllOff();
        }

        private void AllOff()
        {
            foreach (Sustain s in _sustains.Values)
                s.Set(0);
        }

        // ---- event mapping ---------------------------------------------------

        // Normalisation constants are taken from the original MechVibe engine so
        // the feel matches: AutocannonsAndRifles.cs (impulse 350..6000),
        // Missiles.cs (impulse cap 12000), Lasers.cs (DPS factor 10), and the
        // per-type damage caps in *Damage.cs.
        private const float ProjectileMinImpulse = 350f;
        private const float ProjectileMaxImpulse = 6000f;
        private const float MissileMaxImpulse    = 12000f;
        private const float MissileSpeedFactor   = 20000f;
        private const float LaserDpsFactor       = 10f;
        private const float DamageCapTrace       = 10f;
        private const float DamageCapProjectile  = 20f;
        private const float DamageCapMissile     = 2.4f;
        private const float DamageCapMelee       = 80f;
        private const float DamageCapExplosion   = 20f;
        private const float LandedMaxForce       = 2000f;

        private void Handle(EventData e)
        {
            switch (e.EventCode)
            {
                case -2: // ClearFX
                    AllOff();
                    break;

                case -1: // BridgeClosed
                    AllOff();
                    _connected = false;
                    break;

                case 1: // TorsoTwist - Int0 tons, Float2/3 yaw/pitch rate, Float4/5 angles
                {
                    _mechTons  = e.Int0;
                    _torsoYaw   = e.Float4;
                    _torsoPitch = e.Float5;

                    // Float2/3 carry actual angular rates (see the protocol note in
                    // the Lua mod - Clans gives us velocities directly, so unlike the
                    // original there's no differencing to do here).
                    float yawRate   = Math.Abs(e.Float2);
                    float pitchRate = Math.Abs(e.Float3);
                    float rate      = Math.Max(yawRate, pitchRate);

                    float level;
                    if (rate <= (float)Settings.TorsoTwistDeadzone)
                    {
                        level = 0f;
                    }
                    else
                    {
                        // Same shape as TorsoTwist.cs: rate relative to maximum,
                        // weighted by mass.
                        float pct       = Clamp01(rate / (float)Settings.TorsoTwistMaxRate);
                        float massRatio = Clamp01((e.Int0 - 20f) / 80f);
                        float tmf       = (float)Settings.TorsoTwistMassFactor;
                        level = pct * Clamp01((1f - tmf) + tmf * massRatio)
                                    * (float)Settings.TorsoTwistStrength;
                    }

                    _lastTwistTick = Environment.TickCount;
                    S("Move.TorsoTwist").SetRamped(level, Settings.TorsoTwistFade);
                    break;
                }

                case 2: // Footstep - Int0 MassInTons, Float1 SpeedInKmh
                {
                    _mechTons = e.Int0;
                    _speedKmh = e.Float1;

                    // A "force jets off on any footfall" safety net used to sit here
                    // (same idea as the one in Landed below), on the assumption that
                    // a footstep means solid ground and jets can't still be running.
                    // Wrong: bridge.log caught a footstep firing 4 packets after
                    // JumpJets went active (1) - almost certainly the last footfall
                    // of the takeoff itself - which zeroed the channel immediately,
                    // 31 packets before the real JumpJets(0) arrived. The mech was
                    // airborne (torso twist, no more footsteps) for that whole gap,
                    // silently. JumpJetState polling (case 9) is already the
                    // documented reliable source for on/off, so this only ever
                    // introduced a false negative - removed rather than patched.
                    //
                    // Heavier hits harder (mass), and faster movement means a
                    // lighter, quicker footfall - Footsteps.cs: speedAmpEffect =
                    // Lerp(1.0, SpeedFactor, speedRatio), i.e. it fades TOWARD
                    // SpeedFactor as speed rises. This had the sign flipped on the
                    // initial port (louder at speed instead of softer) despite the
                    // "same shape as Footsteps.cs" comment that used to sit here -
                    // never re-checked against the actual original formula until a
                    // line-by-line diff against MechShakerEngine caught it.
                    float massRatio  = Clamp01((e.Int0 - 20f) / 80f);
                    float speedRatio = Clamp01((e.Float1 + 16f) / 166f);

                    float mf   = (float)Settings.FootstepMassFactor;
                    float sf   = (float)Settings.FootstepSpeedFactor;
                    float step = Clamp01((1f - mf) + mf * massRatio) * Clamp01(1f - sf * speedRatio);

                    P("Move.Footstep").Trigger(step, Settings.Footstep);

                    // Float0 is which foot landed (0 left, 1 right) - an extension the
                    // Lua mod adds in a field the original protocol leaves unused, so
                    // MechVibe.exe is unaffected.
                    P(e.Float0 == 1f ? "Move.Footstep.Right" : "Move.Footstep.Left").Trigger(step, Settings.Footstep);
                    break;
                }

                case 3: // Trace - lasers, MG, flamer, TAG, AMS-as-trace
                {
                    bool  active   = e.Float5 == 1f;
                    float damage   = e.Float0;
                    float rof      = e.Float2;
                    float duration = e.Float3;

                    // Sub-type inference is exactly TraceEvent.cs's.
                    if (duration == -1f && damage == 0f)
                    {
                        // TAG holds a steady beam - no rate of fire. Confirmed in
                        // game, so the original's classification was right.
                        float tagLevel = (float)Settings.TagStrength;
                        SetContinuous("Weapon.TAG", active, tagLevel, 0f);
                        FireSustain("TAG", e.Int0, tagLevel, active, 0f);
                    }
                    else if (duration == -1f && damage > 0f)
                    {
                        // AMS fires in bursts; pulse it at its rate of fire.
                        SetContinuous("Weapon.AMS", active, 1f, rof);
                        FireSustain("AMS", e.Int0, 1f, active, rof);
                    }
                    else if (duration == 0f && rof != 0f)
                    {
                        SetContinuous("Weapon.MachineGun", active, 1f, rof);
                        FireSustain("MachineGun", e.Int0, 1f, active, rof);
                    }
                    else if (duration == 0f && rof == 0f)
                    {
                        // Flamer is a continuous jet - held, never pulsed.
                        SetContinuous("Weapon.Flamer", active, 1f, 0f);
                        FireSustain("Flamer", e.Int0, 1f, active, 0f);
                    }
                    else if (active)
                    {
                        // Laser: intensity from damage-per-second, decaying over the
                        // beam's own duration.
                        float dps       = duration > 0f ? damage / duration : damage;
                        float intensity = Clamp01(dps / LaserDpsFactor);

                        // Unlike other weapons the length comes from the game (the
                        // real beam duration), so Min/Max act as bounds rather than
                        // an intensity ramp. The attack/decay shape is still the
                        // configured one.
                        double beam = duration * Settings.LaserDurationScale;
                        if (beam < Settings.Laser.MinLength) beam = Settings.Laser.MinLength;
                        if (beam > Settings.Laser.MaxLength) beam = Settings.Laser.MaxLength;

                        // Float4 flags a pulse laser (set from the weapon's component
                        // name in the Lua mod - the emitter stats can't tell them
                        // apart). A pulse laser gets a burst of short hits spread
                        // across the beam instead of one long one.
                        if (e.Float4 == 1f)
                        {
                            int shots = (int)(beam * Settings.PulseLaserRate);
                            if (shots < 2) shots = 2;
                            double interval = beam / shots;

                            B("Weapon.PulseLaser").Fire(shots, interval, intensity, Settings.PulseLaser);
                            FireBurst("PulseLaser", e.Int0, shots, interval, intensity, Settings.PulseLaser);
                        }
                        else
                        {
                            var laserShape = new EffectShape(beam, beam,
                                                             Settings.Laser.AttackEnd,
                                                             Settings.Laser.DecayStart, 1.0);
                            P("Weapon.Laser").Trigger(intensity, laserShape);
                            FirePulse("Laser", e.Int0, intensity, laserShape);
                        }
                    }
                    break;
                }

                case 4: // Projectile - Float4 sub-type, Float5 impulse
                {
                    float impulse = e.Float5;

                    // Float4 used to be a plain is-PPC flag; it now carries the
                    // ballistic sub-type the Lua mod worked out from the weapon's
                    // asset name. 0 is still PPC, so the old meaning survives.
                    int  subtype = (int)e.Float4;
                    bool isPPC   = subtype == PROJ_PPC;
                    bool isGauss = subtype == PROJ_GAUSS;

                    // Float0/Float1/Float2 are NumberOfTimesToFire, DelayBetweenFiring,
                    // NumberOfProjectiles.
                    int   shots  = (int)e.Float0;
                    float gap    = e.Float1;
                    float rounds = e.Float2 > 1f ? e.Float2 : 1f;

                    // What actually determines the feel is the AMMO MODE fired, not
                    // the weapon platform: an LB-X loaded with slugs is a solid hit
                    // indistinguishable from a plain AC, and a UAC on slug ammo never
                    // bursts. So routing goes by rounds/shots, and the weapon-specific
                    // channels (LBX, UAC) only light up for their signature ammo.
                    // Gauss is always its own channel - it has neither a cluster nor
                    // a burst mode, just one very hard hit.
                    bool isCluster = rounds > 1f;               // LB-X buckshot
                    bool isBurst   = shots > 1 && gap > 0f;      // UAC double/triple-tap

                    string projType;
                    if (isPPC)          projType = "PPC";
                    else if (isGauss)   projType = "Gauss";
                    else if (isCluster) projType = "LBX";
                    else if (isBurst)   projType = ProjectileTypeName(subtype); // UAC or RAC
                    else                projType = "Projectile"; // plain AC, and any slug ammo

                    // Cluster weapons fire several rounds per shot and the original
                    // multiplies them in (AutocannonsAndRifles.cs: Impulse * rounds).
                    // Without this an LB-X felt exactly like a solid AC.
                    float total = impulse * rounds;

                    // Only PPC needs its own ceiling. Confirmed in game: a Clan ER
                    // PPC reports 1200 against the shared 6000 ceiling (15%), while a
                    // Clan Gauss Rifle reports 3000 (47%) - already the harder hit the
                    // slug should be, on the shared scale, with no special-casing.
                    float ceiling = isPPC ? (float)Settings.PpcFullImpulse : ProjectileMaxImpulse;
                    float span    = ceiling - ProjectileMinImpulse;
                    float ratio   = span > 0f
                        ? Clamp01((total - ProjectileMinImpulse) / span)
                        : 1f;

                    // Floor so a small-caliber weapon's real recoil isn't rounded
                    // down to nothing - see ProjectileMinAmplitude.
                    float minAmp    = (float)Settings.ProjectileMinAmplitude;
                    float intensity = ratio * (1f - minAmp) + minAmp;

                    EffectShape projShape = isPPC ? Settings.PPC : Settings.Projectile;

                    if (isBurst)
                    {
                        // A burst needs its own envelope: the single-shot one is
                        // longer than the gap between rounds, so every impulse would
                        // overlap the next and the whole burst would feel like one
                        // long hit.
                        B("Weapon." + projType).Fire(shots, gap, intensity, Settings.ProjectileBurst);
                        FireBurst(projType, e.Int0, shots, gap, intensity, Settings.ProjectileBurst);
                    }
                    else
                    {
                        P("Weapon." + projType).Trigger(intensity, projShape);
                        FirePulse(projType, e.Int0, intensity, projShape);
                    }

                    if (isPPC)
                    {
                        // The original builds a PPC from exactly two layers (PPCs.cs):
                        // a 30Hz/120ms pop at full strength for the discharge, and an
                        // 85Hz/700ms tail at half strength for the plasma's ringing.
                        //
                        // The pop is fired through the side channels as well, so the
                        // discharge can be felt in the arm it came from. The tail is
                        // ringing rather than impact - it has no direction, so it
                        // stays on the combined channel only.
                        FirePulse("PPC.Pop", e.Int0, intensity, Settings.PPCPop);
                        P("Weapon.PPC.Tail").Trigger(intensity, Settings.PPCTail);
                    }
                    break;
                }

                case 5: // Missiles - Float0 count, Float1 interval, Float2 speed, Float3 impulse
                {
                    float impulse  = e.Float3;
                    float speed    = e.Float2;
                    int   count    = e.Float0 < 1f ? 1 : (int)e.Float0;
                    float interval = e.Float1;

                    // Exposed so a formula can tell an LRM20 from an SRM2.
                    _lastMissileCount = count;
                    _lastMissileSalvo = interval <= 0f ? 1 : 0;

                    // Missiles.cs: a salvo (interval 0) totals its impulse into one
                    // hit; a stream fires one impulse per missile at reduced strength.
                    bool  salvo         = interval <= 0f;
                    float totalImpulse  = salvo ? impulse * count : impulse;

                    // impulseFactor = sqrt(impulse/max * speed/speedFactor) - speed was
                    // being ignored before, so fast missiles felt the same as slow ones.
                    float ratio     = (totalImpulse / MissileMaxImpulse) * (speed / MissileSpeedFactor);
                    float intensity = Clamp01((float)Math.Sqrt(ratio < 0 ? 0 : ratio));

                    P("Weapon.Missile").Trigger(intensity, Settings.Missile);
                    FirePulse("Missile", e.Int0, intensity, Settings.Missile);

                    if (salvo)
                    {
                        P("Weapon.Missile.Launch").Trigger(intensity, Settings.MissileLaunch);
                        FirePulse("Missile.Launch", e.Int0, intensity, Settings.MissileLaunch);
                        P("Weapon.Missile.Tail").Trigger(intensity, Settings.MissileTail);
                    }
                    else
                    {
                        int capped = count > (int)Settings.MissileStreamMax
                            ? (int)Settings.MissileStreamMax : count;
                        float streamed = intensity * (float)Settings.MissileStreamFactor;

                        B("Weapon.Missile.Launch").Fire(capped, interval, streamed, Settings.MissileLaunch);
                        FireBurst("Missile.Launch", e.Int0, capped, interval, streamed, Settings.MissileLaunch);
                        B("Weapon.Missile.Tail").Fire(capped, interval, streamed, Settings.MissileTail);
                    }
                    break;
                }

                case 6: // AMS - Float0 rate of fire, Float5 active
                {
                    bool  amsActive = e.Float5 == 1f;
                    float amsRof    = e.Float0;
                    SetContinuous("Weapon.AMS", amsActive, 1f, amsRof);
                    FireSustain("AMS", e.Int0, 1f, amsActive, amsRof);
                    break;
                }

                case 7: // Melee - Int0 tons, Float0 IsHit (1 = connected, 0 = swing), Float1 WeaponId
                {
                    _mechTons = e.Int0;

                    float mass     = Clamp01((e.Int0 - 20f) / 80f);
                    float mmf      = (float)Settings.MeleeMassFactor;
                    float force    = Clamp01((1f - mmf) + mmf * mass);
                    int   weaponId = (int)e.Float1;

                    // The original splits melee into the swing and the connect, with
                    // the swing much longer and softer.
                    if (e.Float0 == 1f)
                    {
                        // Both driven by MeleeHit - the two shapes were identical and
                        // having a separate slider for each was just confusing.
                        P("Weapon.Melee").Trigger(force, Settings.MeleeHit);
                        P("Weapon.Melee.Hit").Trigger(force, Settings.MeleeHit);
                        FirePulse("Melee.Hit", weaponId, force, Settings.MeleeHit);
                    }
                    else
                    {
                        P("Weapon.Melee.Swing").Trigger(force, Settings.MeleeSwing);
                        FirePulse("Melee.Swing", weaponId, force, Settings.MeleeSwing);
                    }
                    break;
                }

                case 8: // Dropship - Int0 stage
                    P("Event.Dropship").Trigger(1f, Settings.Dropship);
                    break;

                case 9: // JumpJets - Int0 active
                {
                    // Fades rather than switches: the original spools jets down over
                    // 1.5s (TransitionOffTime), which is most of how a jet reads.
                    bool jetsOn = e.Int0 != 0;
                    S("Move.JumpJets").SetRamped(
                        jetsOn ? (float)Settings.JumpJetsStrength : 0f,
                        jetsOn ? Settings.JumpJetsFadeIn : Settings.JumpJetsFadeOut);
                    break;
                }

                case 10: // Airborne
                    S("Move.Airborne").Set(e.Int0 != 0 ? 1f : 0f);
                    break;

                case 11: // Landed - Int0 tons, Float0 acceleration in km/h^2
                {
                    _mechTons = e.Int0;

                    // Touching down ends the jets, whatever the game did or didn't
                    // report. Tapping them gives a clean 1/0 pair, but holding them
                    // to the end of the fuel produced 1,1 and no stop event at all -
                    // leaving a sustained channel latched on forever.
                    Sustain jets = S("Move.JumpJets");
                    if (jets.Value > 0f)
                        jets.SetRamped(0f, Settings.JumpJetsFadeOut);

                    // LandingImpacts.cs: force = mass(kg) * accel(m/s^2), capped, sqrt'd.
                    float mass  = e.Int0 * 1000f;
                    float accel = e.Float0 * (1000f / (3600f * 3600f));
                    float force = Math.Abs(mass * accel);
                    if (force > LandedMaxForce) force = LandedMaxForce;
                    P("Move.Landed").Trigger((float)Math.Sqrt(force / LandedMaxForce), Settings.Landed);
                    break;
                }

                case 12: // MASC - Int0 engaged, Float0 gauge
                {
                    bool mascOn = e.Int0 != 0;

                    // Accept either 0-1 or 0-100 and normalise.
                    float gauge = e.Float0;
                    if (gauge > 1f) gauge = gauge / 100f;
                    gauge = Clamp01(gauge);

                    // Clans only reports on/off (OnMASCEngaged), so the gauge arrives
                    // as 0 and there is nothing to scale by. Treating "no gauge" as
                    // full strength - which is what this did before - made MASC the
                    // loudest thing in the game. It's a flat level now, and the gauge
                    // only modulates it if a build ever does report one.
                    float level = 0f;
                    if (mascOn)
                    {
                        level = (float)Settings.MascStrength;
                        if (gauge > 0f)
                            level *= (float)Math.Pow(gauge, Settings.MascGaugeExponent);
                    }

                    S("Move.MASC").SetRamped(level,
                        mascOn ? Settings.MascFadeIn : Settings.MascFadeOut);
                    break;
                }

                case 13: // Powering - Int0 0 init / 1 up / 2 down
                {
                    _powerState = e.Int0;

                    // Each stage has its own length in the original: 5s / 4s / 3.3s.
                    EffectShape powerShape;
                    string      stageChannel;
                    switch (e.Int0)
                    {
                        case 0:  powerShape = Settings.PoweringInit; stageChannel = "Event.Powering.Init"; break;
                        case 1:  powerShape = Settings.PoweringUp;   stageChannel = "Event.Powering.Up";   break;
                        default: powerShape = Settings.PoweringDown; stageChannel = "Event.Powering.Down"; break;
                    }

                    P("Event.Powering").Trigger(1f, powerShape);
                    P(stageChannel).Trigger(1f, powerShape);
                    break;
                }

                case 14: // PartDestruction - Int0 part enum (EMechParts, same values)
                {
                    _lastPartDestroyed = e.Int0;
                    // Weighted by which part was lost, as the original does: a head
                    // or centre torso hits full force, an arm noticeably less.
                    float partForce = (float)Settings.PartFactorFor(e.Int0);
                    P("Damage.PartDestruction").Trigger(partForce, Settings.PartDestruction);
                    P(PartSideChannel(e.Int0)).Trigger(partForce, Settings.PartDestruction);
                    if (e.Int0 >= 0 && e.Int0 < MechParts.Length)
                        P("Damage.PartDestruction." + MechParts[e.Int0]).Trigger(partForce, Settings.PartDestruction);

                    // Two layers in the original: a 700ms crack and a 1500ms rumble.
                    P("Damage.PartDestruction.Primary").Trigger(partForce, Settings.PartDestructionPrimary);
                    P("Damage.PartDestruction.Secondary").Trigger(partForce, Settings.PartDestructionSecondary);
                    break;
                }

                case 15: // Damaged - Int0 type, Float0 damage
                {
                    float damage = e.Float0;
                    _lastDamage     = damage;
                    _lastDamageType = e.Int0;

                    // Each damage category has its own cap and its own envelope,
                    // matching the original's separate *Damage effects.
                    float       intensity;
                    string      channel;
                    EffectShape shape;
                    switch (e.Int0)
                    {
                        case 0:
                            channel = "Damage.Trace";
                            // LaserDamage.cs works in DPS over a 0.5s tick.
                            intensity = Clamp01(damage / 0.5f / LaserDpsFactor);
                            shape     = Settings.DamageTrace;
                            break;
                        case 1:
                            channel   = "Damage.Projectile";
                            intensity = Clamp01(damage / DamageCapProjectile);
                            shape     = Settings.DamageProjectile;
                            break;
                        case 2:
                            channel   = "Damage.Missile";
                            intensity = Clamp01(damage / DamageCapMissile);
                            shape     = Settings.DamageMissile;
                            break;
                        case 3:
                            channel   = "Damage.Melee";
                            intensity = Clamp01(damage / DamageCapMelee);
                            shape     = Settings.DamageMelee;
                            break;
                        default:
                            channel   = "Damage.Explosion";
                            intensity = Clamp01(damage / DamageCapExplosion);
                            shape     = Settings.DamageExplosion;
                            break;
                    }

                    P(channel).Trigger(intensity, shape);
                    P("Damage.Any").Trigger(intensity, shape);
                    break;
                }

                case 16: // Impulse [Clans extension] - Int0 direction, Float0 magnitude,
                {        //   Float1/2/3 = from-front / from-right / from-above (-1..1)
                    float mag = e.Float0;
                    if (mag < (float)Settings.ImpulseMinMagnitude) break;

                    _impactForward = e.Float1;
                    _impactRight   = e.Float2;

                    float force = Clamp01(mag / (float)Settings.ImpulseMaxMagnitude);

                    P("Impact.Any").Trigger(force, Settings.Impulse);
                    P(ImpactChannel(e.Int0)).Trigger(force, Settings.Impulse);

                    // Also split across the two horizontal axes by how much of the
                    // shock each one accounts for, so a hit from the front-right
                    // drives both channels rather than only the dominant one.
                    float fwd   = e.Float1;
                    float right = e.Float2;
                    if (fwd > 0)   P("Impact.Front").Trigger(force * fwd, Settings.Impulse);
                    if (fwd < 0)   P("Impact.Rear").Trigger(force * -fwd, Settings.Impulse);
                    if (right > 0) P("Impact.Right").Trigger(force * right, Settings.Impulse);
                    if (right < 0) P("Impact.Left").Trigger(force * -right, Settings.Impulse);
                    break;
                }
            }
        }

        /// <summary>
        /// EMechParts values, as encoded by the Lua mod into WeaponId
        /// (MechPart*1000 + SlotId). Verified against the SDK dump:
        /// Head=0, CenterTorso=1, LeftTorso=2, LeftArm=3, LeftLeg=4,
        /// RightTorso=5, RightArm=6, RightLeg=7.
        /// </summary>
        private static string SideName(int weaponId)
        {
            int part = weaponId / 1000;
            switch (part)
            {
                case 2: case 3: case 4: return "Left";
                case 5: case 6: case 7: return "Right";
                default:                return "Center";
            }
        }

        /// <summary>Impulse direction code from the Lua mod (EventCode 16, Int0).</summary>
        private static string ImpactChannel(int direction)
        {
            switch (direction)
            {
                case 0:  return "Impact.Front";
                case 1:  return "Impact.Right";
                case 2:  return "Impact.Rear";
                case 3:  return "Impact.Left";
                case 4:  return "Impact.Above";
                default: return "Impact.Below";
            }
        }

        private static string PartSideChannel(int mechPart)
        {
            switch (mechPart)
            {
                case 2: case 3: case 4: return "Damage.PartDestruction.Left";
                case 5: case 6: case 7: return "Damage.PartDestruction.Right";
                default:                return "Damage.PartDestruction.Center";
            }
        }

        /// <summary>
        /// Routes a momentary weapon event to both position channels: the
        /// type-agnostic side channel ("anything from the left") and the precise
        /// type-on-side channel ("a left laser").
        /// </summary>
        private void FirePulse(string type, int weaponId, float intensity, EffectShape shape)
        {
            string side = SideName(weaponId);
            P("Weapon." + side).Trigger(intensity, shape);
            P("Weapon." + side + "." + type).Trigger(intensity, shape);
        }

        /// <summary>
        /// Same as FirePulse but for weapons that fire a series of shots - a pulse
        /// laser's burst, or an autocannon firing several rounds per trigger pull.
        /// The side channel still gets one hit; the type channel gets the whole burst.
        /// </summary>
        private void FireBurst(string type, int weaponId, int count, double interval,
                               float intensity, EffectShape shape)
        {
            string side = SideName(weaponId);
            P("Weapon." + side).Trigger(intensity, shape);
            B("Weapon." + side + "." + type).Fire(count, interval, intensity, shape);
        }

        /// <summary>
        /// Same for continuous weapons. The side channel still gets a pulse - it only
        /// hears about trigger pulls - but the type-on-side channel is a level, so
        /// "left machine gun" can shake for as long as the trigger is held.
        /// </summary>
        private void FireSustain(string type, int weaponId, float level, bool active, float rateOfFire)
        {
            string side = SideName(weaponId);
            SetContinuous("Weapon." + side + "." + type, active, level, rateOfFire);
            if (active)
                P("Weapon." + side).Trigger(level, Settings.ContinuousPulse);
        }

        private static float Clamp01(float v)
        {
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
        }

        // ---- value holders ---------------------------------------------------

        /// <summary>
        /// A momentary hit that fades linearly. Read from SimHub's thread while the
        /// poller writes, so the fields are volatile and the decay is computed from
        /// a timestamp rather than ticked.
        /// </summary>
        private class Pulse
        {
            private volatile float _peak;
            private long           _startTicks;
            private double         _length     = 0.25;
            private double         _attackEnd  = 0.0;
            private double         _decayStart = 0.3;

            /// <summary>
            /// Fires with an explicit envelope, scaled by intensity.
            ///
            /// The shape's Amplitude (and AmplitudeExponent curve) are folded into the
            /// peak here via shape.CurveAmplitude(), rather than duplicating that math
            /// inline. This class walks its own attack/sustain/decay rather than
            /// calling shape.Evaluate(), so anything Evaluate does has to be repeated
            /// by hand - Amplitude itself was missed here once already (the Strength
            /// slider did nothing for every single-shot effect until that was caught),
            /// which is exactly why CurveAmplitude is a shared method instead of a
            /// second copy of the exponent math. Burst goes through Evaluate and was
            /// never affected by either gap.
            /// </summary>
            public void Trigger(float peak, EffectShape shape)
            {
                _length     = shape.LengthFor(peak);
                _attackEnd  = Clamp01(shape.AttackEnd);
                _decayStart = Clamp01(shape.DecayStart);
                if (_decayStart < _attackEnd) _decayStart = _attackEnd;

                double scaled = shape.CurveAmplitude(peak) * shape.Amplitude;
                if (scaled > 1.0) scaled = 1.0;   // Amplitude may exceed 1 on purpose
                if (scaled < 0.0) scaled = 0.0;
                _peak = (float)scaled;

                Interlocked.Exchange(ref _startTicks, Stopwatch.GetTimestamp());
            }

            /// <summary>Fires with a fixed duration and the default envelope.</summary>
            public void Trigger(float peak, double lengthSeconds)
            {
                _length     = lengthSeconds <= 0 ? 0.25 : lengthSeconds;
                _attackEnd  = 0.0;
                _decayStart = 0.3;
                _peak       = peak;
                Interlocked.Exchange(ref _startTicks, Stopwatch.GetTimestamp());
            }

            public float Value
            {
                get
                {
                    long start = Interlocked.Read(ref _startTicks);
                    if (start == 0) return 0f;

                    double elapsed = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
                    double x       = elapsed / _length;
                    if (x >= 1.0) return 0f;

                    // Attack -> sustain -> decay, the same shape MechVibe's
                    // ImpulseGenerator uses (AttackEnd / DecayStart as fractions).
                    if (x < _attackEnd)
                        return (float)(_peak * (x / _attackEnd));

                    if (x < _decayStart)
                        return _peak;

                    double tail = 1.0 - _decayStart;
                    if (tail <= 0) return 0f;
                    return (float)(_peak * (1.0 - (x - _decayStart) / tail));
                }
            }

            private static double Clamp01(double v)
            {
                if (v < 0) return 0;
                if (v > 1) return 1;
                return v;
            }
        }

        /// <summary>
        /// A finite series of impulses spaced by a fixed interval - one per missile in
        /// a stream launch. Missiles.cs schedules exactly this: when MissileInterval
        /// is non-zero it fires NumberOfMissiles impulses at reduced amplitude instead
        /// of one big hit, which is what an LRM salvo actually feels like.
        /// </summary>
        private class Burst
        {
            private volatile float  _peak;
            private volatile int    _count;
            private double          _interval;
            private long            _startTicks;
            private EffectShape     _shape = new EffectShape(0.1, 0.1, 0, 0.3, 1.0);
            private double          _totalLength;

            public void Fire(int count, double interval, float peak, EffectShape shape)
            {
                _shape    = shape;
                _peak     = peak;
                _interval = interval < 0 ? 0 : interval;
                _count    = count < 1 ? 1 : count;
                // Past this point every impulse has finished.
                _totalLength = _interval * (_count - 1) + shape.LengthFor(peak);
                Interlocked.Exchange(ref _startTicks, Stopwatch.GetTimestamp());
            }

            public float Value
            {
                get
                {
                    long start = Interlocked.Read(ref _startTicks);
                    if (start == 0) return 0f;

                    double elapsed = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
                    if (elapsed >= _totalLength) return 0f;

                    // Impulses can overlap when the interval is short, so take the
                    // loudest rather than summing - summing would clip immediately.
                    EffectShape shape = _shape;
                    float peak  = _peak;
                    int   count = _count;
                    float best  = 0f;

                    for (int i = 0; i < count; i++)
                    {
                        double t = elapsed - i * _interval;
                        if (t < 0) break;             // later impulses haven't fired
                        float v = shape.Evaluate(t, peak);
                        if (v > best) best = v;
                    }

                    return best;
                }
            }
        }

        /// <summary>
        /// A level that holds until it is set again, optionally ramping to the new
        /// value instead of jumping. The original fades sustained effects via
        /// TransitionOnTime/TransitionOffTime - jump jets take 1.5s to die away, and
        /// switching them off instantly sounds wrong.
        /// </summary>
        private class Sustain
        {
            private volatile float _from;
            private volatile float _to;
            private long           _startTicks;
            private double         _rampSeconds;

            /// <summary>Sets instantly.</summary>
            public void Set(float v)
            {
                _from = v;
                _to   = v;
                _rampSeconds = 0;
                Interlocked.Exchange(ref _startTicks, Stopwatch.GetTimestamp());
            }

            /// <summary>Ramps from wherever it currently is to the new value.</summary>
            public void SetRamped(float v, double rampSeconds)
            {
                if (rampSeconds <= 0) { Set(v); return; }
                _from = Value;
                _to   = v;
                _rampSeconds = rampSeconds;
                Interlocked.Exchange(ref _startTicks, Stopwatch.GetTimestamp());
            }

            public float Value
            {
                get
                {
                    if (_rampSeconds <= 0) return _to;

                    long start = Interlocked.Read(ref _startTicks);
                    double elapsed = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
                    if (elapsed >= _rampSeconds) return _to;

                    return _from + (_to - _from) * (float)(elapsed / _rampSeconds);
                }
            }
        }

        /// <summary>
        /// A level that pulses at a fixed rate while active - one pulse per shot,
        /// spaced by 1/RateOfFire, mirroring MachineGuns.cs and AMS.cs. Holding a
        /// steady level instead would lose the weapon's cadence entirely.
        /// </summary>
        private class Repeater
        {
            private volatile bool  _active;
            private volatile float _level;
            private double         _period = 0.1;
            private double         _width  = 0.45;
            private long           _startTicks;

            public void Start(float level, double shotsPerSecond, double width, double maxRate)
            {
                if (shotsPerSecond <= 0) shotsPerSecond = 1;
                if (shotsPerSecond > maxRate) shotsPerSecond = maxRate;

                _period = 1.0 / shotsPerSecond;
                _width  = width <= 0 ? 0.45 : (width > 1 ? 1 : width);
                _level  = level;
                if (!_active)
                {
                    Interlocked.Exchange(ref _startTicks, Stopwatch.GetTimestamp());
                    _active = true;
                }
            }

            public void Stop() { _active = false; }

            public float Value
            {
                get
                {
                    if (!_active) return 0f;

                    long start = Interlocked.Read(ref _startTicks);
                    double elapsed = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;

                    // Position within the current shot interval.
                    double phase = (elapsed % _period) / _period;
                    if (phase >= _width) return 0f;

                    // Sharp attack, linear fall - a hit rather than a tone.
                    return _level * (float)(1.0 - phase / _width);
                }
            }
        }

        // ---- wire structs ----------------------------------------------------
        // Must match MechVibeBridge byte for byte.

        [StructLayout(LayoutKind.Sequential)]
        private struct ControlBlock
        {
            public long WriteIndex;
            public long PacketNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EventData
        {
            public int   EventCode;
            public int   Int0;
            public float Float0;
            public float Float1;
            public float Float2;
            public float Float3;
            public float Float4;
            public float Float5;
        }
    }
}
