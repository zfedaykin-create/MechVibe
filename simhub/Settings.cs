using System;

namespace MechVibe
{
    /// <summary>
    /// The time-domain shape of one momentary effect.
    ///
    /// ShakeIt owns frequency and gain; it has no envelope of its own, so the
    /// attack/sustain/decay shape and the duration live here. The model and the
    /// defaults are taken from MechVibe's own settings (DefaultSettings.yaml) so
    /// the feel matches the original engine:
    ///
    ///     amplitude
    ///        |      ___________
    ///        |     /           \
    ///        |    /             \
    ///        |   /               \
    ///        +--+-----+----------+---- time
    ///           0  AttackEnd  DecayStart  1        (fractions of Length)
    ///
    /// Length itself scales with how hard the event was:
    ///     Length = Min + (Max - Min) * intensity^LengthExponent
    /// An exponent below 1 makes even weak hits fairly long, which is what the
    /// original uses for damage effects.
    /// </summary>
    public class EffectShape
    {
        /// <summary>Duration at intensity 0, in seconds.</summary>
        public double MinLength;

        /// <summary>Duration at intensity 1, in seconds.</summary>
        public double MaxLength;

        /// <summary>Fraction of the duration spent ramping up (0 = instant hit).</summary>
        public double AttackEnd;

        /// <summary>Fraction of the duration at which the fade-out begins.</summary>
        public double DecayStart;

        /// <summary>Curve applied to intensity when picking the duration. 1 = linear.</summary>
        public double LengthExponent;

        /// <summary>
        /// Curve applied to intensity before it becomes peak amplitude. 1 = linear.
        /// The original's damage effects use a SEPARATE, usually much lower exponent
        /// here than for length (e.g. ExplosionDamage: length 0.15, amplitude 0.08) -
        /// missed on the initial port, since EffectShape only had one exponent slot
        /// and LengthExponent absorbed both roles. Below 1, weak hits stay audible
        /// instead of trailing off linearly toward silence.
        /// </summary>
        public double AmplitudeExponent = 1.0;

        /// <summary>
        /// Relative strength of this layer. Used by the two-layer effects, where the
        /// original gives the tail its own amplitude (missile tail 100%, PPC tail 50%,
        /// melee swing 75%, destruction secondary 90%).
        /// </summary>
        public double Amplitude = 1.0;

        public EffectShape() { }

        public EffectShape(double minLength, double maxLength, double attackEnd,
                           double decayStart, double lengthExponent)
            : this(minLength, maxLength, attackEnd, decayStart, lengthExponent, 1.0)
        {
        }

        public EffectShape(double minLength, double maxLength, double attackEnd,
                           double decayStart, double lengthExponent, double amplitude)
        {
            MinLength      = minLength;
            MaxLength      = maxLength;
            AttackEnd      = attackEnd;
            DecayStart     = decayStart;
            LengthExponent = lengthExponent;
            Amplitude      = amplitude;
        }

        /// <summary>
        /// Evaluates the envelope at a point in time. Shared by the single-shot and
        /// burst channels so both ring out identically.
        /// </summary>
        public float Evaluate(double elapsedSeconds, float peak)
        {
            if (elapsedSeconds < 0) return 0f;

            double length = LengthFor(peak);
            double x      = elapsedSeconds / length;
            if (x >= 1.0) return 0f;

            double attack = Clamp01(AttackEnd);
            double decay  = Clamp01(DecayStart);
            if (decay < attack) decay = attack;

            // Amplitude can exceed 1 to lift a weapon the game reports weakly, so the
            // result has to be capped - ShakeIt expects 0-100 and clips anything over.
            double scaled = CurveAmplitude(peak) * Amplitude;
            if (scaled > 1.0) scaled = 1.0;

            if (x < attack)
                return attack <= 0 ? (float)scaled : (float)(scaled * (x / attack));

            if (x < decay)
                return (float)scaled;

            double tail = 1.0 - decay;
            if (tail <= 0) return 0f;
            return (float)(scaled * (1.0 - (x - decay) / tail));
        }

        /// <summary>
        /// Applies AmplitudeExponent to a 0-1 intensity. Pulled out so Pulse.Trigger
        /// (which computes its own envelope and never calls Evaluate - see the
        /// Amplitude comment on Trigger) can apply the exact same curve instead of a
        /// second copy that could drift out of sync or get missed entirely, which is
        /// exactly how Amplitude itself went unused for every single-shot effect
        /// before that was caught.
        /// </summary>
        public double CurveAmplitude(double peak)
        {
            peak = Clamp01(peak);
            return AmplitudeExponent == 1.0 ? peak : Math.Pow(peak, AmplitudeExponent);
        }

        private static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }

        public double LengthFor(double intensity)
        {
            if (intensity < 0) intensity = 0;
            if (intensity > 1) intensity = 1;

            double scaled = LengthExponent == 1.0
                ? intensity
                : Math.Pow(intensity, LengthExponent);

            double len = MinLength + (MaxLength - MinLength) * scaled;
            return len < 0.02 ? 0.02 : len;
        }
    }

    /// <summary>
    /// Everything the settings page can edit. Defaults mirror MechVibe's
    /// DefaultSettings.yaml (ms converted to seconds, percentages to fractions), so
    /// out of the box the shakes have the same timing as the original mod.
    /// </summary>
    public class MW5ClansSettings
    {
        // --- weapons fired ---------------------------------------------------

        /// <summary>AutocannonsAndRifles: 140-800ms, attack 5%, decay 30%.</summary>
        [EffectGroup("AC / Gauss", Section.Weapons)]
        public EffectShape Projectile = new EffectShape(0.14, 0.80, 0.05, 0.30, 1.0);

        /// <summary>
        /// Used instead of the above when a weapon fires several rounds per pull
        /// (UAC, rotary AC). Their gap is around 50ms, so a 140ms+ impulse would
        /// bury the burst under one long buzz and it would feel like a single shot.
        ///
        /// At these frequencies 50ms is barely two cycles, so the rounds can't be
        /// separated the way pulse laser hits are - the cone never gets to stop.
        /// The aim is different here: keep it running but let the amplitude dip
        /// between rounds, which is what reads as a rattle.
        /// </summary>
        [EffectGroup("AC burst (UAC / rotary)", Section.Weapons)]
        public EffectShape ProjectileBurst = new EffectShape(0.035, 0.050, 0.00, 0.45, 1.0);

        /// <summary>PPCs tail: 700ms, attack 40%, decay 40% - a softer, rounder hit.</summary>
        [EffectGroup("PPC (unused - see PPC pop/tail)", Section.Weapons)]
        public EffectShape PPC = new EffectShape(0.20, 0.70, 0.40, 0.40, 1.0);

        /// <summary>
        /// Strength for every other weapon comes from the recoil impulse the game
        /// reports, but a PPC is an energy weapon and barely pushes: a Clan ER PPC
        /// reports 1200 where an autocannon reports 2500. Measured against the
        /// shared 6000 ceiling that put it at 15% - far below how the shot reads on
        /// screen. PPCs get their own ceiling instead.
        /// </summary>
        [Setting("PPC: impulse for full strength", 500.0, 6000.0)]
        public double PpcFullImpulse = 1400.0;

        /// <summary>
        /// Floor for ballistic intensity, as a fraction of full strength. The
        /// original does the same (AutocannonsAndRifles.cs, MinAmplitude default
        /// 80%): a linear 0-to-1 ratio against the impulse ceiling makes a
        /// small-caliber weapon nearly silent, because its reported impulse is
        /// tiny next to an AC20's. Confirmed in game - a UAC2 on solid slug ammo
        /// (one hit, no burst to mask it) reported the same 700 impulse as its
        /// burst-fire mode, which normalizes to ~6% and reads as no recoil at all.
        /// Every ballistic hit is guaranteed at least this much; the ceiling only
        /// controls how much further a harder hit climbs above it.
        /// </summary>
        [Setting("Ballistic: minimum strength", 0.0, 1.0, Section.Weapons, "AC / Gauss")]
        public double ProjectileMinAmplitude = 0.60;

        /// <summary>Missiles tail: 800ms, attack 5%, decay 20%.</summary>
        [EffectGroup("Missile", Section.Weapons)]
        public EffectShape Missile = new EffectShape(0.20, 0.80, 0.05, 0.20, 1.0);

        /// <summary>
        /// Envelope for the combined Weapon.Melee channel, which fires alongside
        /// Weapon.Melee.Hit on a connect. Deliberately NOT an [EffectGroup]: it held
        /// the same values as MeleeHit, so the settings screen showed two identical
        /// melee sliders and moving one did nothing visible. MeleeHit drives both now
        /// and this is kept only so the channel keeps its shape.
        /// </summary>
        public EffectShape Melee = new EffectShape(0.25, 0.45, 0.00, 0.40, 1.0);

        /// <summary>
        /// Lasers are driven by the beam's real duration from the game, so Min/Max
        /// act as bounds and LaserDurationScale multiplies the beam time.
        /// </summary>
        [EffectGroup("Laser", Section.Weapons)]
        public EffectShape Laser = new EffectShape(0.10, 1.50, 0.05, 0.25, 1.0);

        [Setting("Laser length x beam duration", 0.1, 5.0, Section.Weapons, "Laser")]
        public double LaserDurationScale = 1.0;

        /// <summary>
        /// A pulse laser fires a burst rather than one held beam. The game doesn't
        /// report how many pulses, so this is how many are assumed per second - the
        /// burst is spread across the beam's real duration.
        /// </summary>
        /// Two competing limits. A shaker's cone keeps moving after the signal
        /// stops, so short gaps run together into one long buzz; but a pulse under
        /// ~50ms is only three or four cycles at these frequencies and barely moves
        /// the cone at all. Both have to clear, which means slowing the rate rather
        /// than shortening the pulse: at 6/sec the impulses are 167ms apart, leaving
        /// a 75ms hit and most of the gap silent.
        [EffectGroup("Pulse laser (each pulse)", Section.Weapons)]
        public EffectShape PulseLaser = new EffectShape(0.055, 0.075, 0.00, 0.55, 1.0);

        [Setting("Pulse laser: pulses per second", 2.0, 20.0, Section.Weapons, "Pulse laser (each pulse)")]
        public double PulseLaserRate = 6.0;

        /// <summary>MachineGuns: 60ms, attack 0%, decay 30%. Also used for the
        /// trigger-pull pulse on the side channels.</summary>
        [EffectGroup("Continuous (MG / flamer / AMS)", Section.Weapons)]
        public EffectShape ContinuousPulse = new EffectShape(0.06, 0.30, 0.00, 0.30, 1.0);

        // --- movement --------------------------------------------------------

        /// <summary>Footsteps: 440ms, attack 10%, decay 75% - a long, soft thud.</summary>
        [EffectGroup("Footstep", Section.Movement)]
        public EffectShape Footstep = new EffectShape(0.20, 0.44, 0.10, 0.75, 1.0);

        /// <summary>LandingImpacts: 800ms, attack 5%, decay 50%.</summary>
        [EffectGroup("Landing impact", Section.Movement)]
        public EffectShape Landed = new EffectShape(0.25, 0.80, 0.05, 0.50, 1.0);

        // --- damage taken ----------------------------------------------------
        // These carry the original's LengthExponent values, which are well below 1:
        // even light hits ring out, heavy ones only somewhat longer.

        /// <summary>
        /// LaserDamage is a DPS accumulator in the original - kept short, and has no
        /// length exponent of its own (length exponent 1.0 here = linear). The 0.45
        /// this used to carry as its length exponent was actually the original's
        /// AmplitudeExponent, misassigned on the initial port back when EffectShape
        /// only had one exponent slot - moved to its correct place below.
        /// </summary>
        [EffectGroup("Hit by laser / MG / flamer", Section.Damage)]
        public EffectShape DamageTrace = new EffectShape(0.05, 0.30, 0.00, 0.30, 1.0) { AmplitudeExponent = 0.45 };

        /// <summary>ProjectileDamage: max 700ms, decay 40%, length exponent 0.4, amplitude exponent 0.2.</summary>
        [EffectGroup("Hit by AC / Gauss / PPC", Section.Damage)]
        public EffectShape DamageProjectile = new EffectShape(0.00, 0.70, 0.00, 0.40, 0.40) { AmplitudeExponent = 0.2 };

        /// <summary>MissileDamage: max 240ms, decay 10%, length exponent 0.15, amplitude exponent 0.3.</summary>
        [EffectGroup("Hit by missiles", Section.Damage)]
        public EffectShape DamageMissile = new EffectShape(0.00, 0.24, 0.00, 0.10, 0.15) { AmplitudeExponent = 0.3 };

        /// <summary>MeleeDamage: max 1150ms, decay 20%, length exponent 0.18, amplitude exponent 0.18.</summary>
        [EffectGroup("Hit by melee", Section.Damage)]
        public EffectShape DamageMelee = new EffectShape(0.00, 1.15, 0.00, 0.20, 0.18) { AmplitudeExponent = 0.18 };

        /// <summary>ExplosionDamage: max 1400ms, decay 20%, length exponent 0.15, amplitude exponent 0.08.</summary>
        [EffectGroup("Hit by explosion", Section.Damage)]
        public EffectShape DamageExplosion = new EffectShape(0.00, 1.40, 0.00, 0.20, 0.15) { AmplitudeExponent = 0.08 };

        /// <summary>PartDestruction: 700ms, attack 0%, decay 85%.</summary>
        [EffectGroup("Part destruction", Section.Damage)]
        public EffectShape PartDestruction = new EffectShape(0.50, 0.70, 0.00, 0.85, 1.0);

        // --- sequences -------------------------------------------------------

        // Powering has three distinct stages in the original, each with its own
        // duration: Init 5000ms, PoweringUp 4000ms, ShuttingDown 3300ms.
        [EffectGroup("Power: initialising", Section.Events)]
        public EffectShape PoweringInit = new EffectShape(5.00, 5.00, 0.10, 0.40, 1.0);

        [EffectGroup("Power: starting up", Section.Events)]
        public EffectShape PoweringUp = new EffectShape(4.00, 4.00, 0.10, 0.40, 1.0);

        [EffectGroup("Power: shutting down", Section.Events)]
        public EffectShape PoweringDown = new EffectShape(3.30, 3.30, 0.10, 0.40, 1.0);

        [EffectGroup("Dropship", Section.Events)]
        public EffectShape Dropship = new EffectShape(0.60, 2.00, 0.15, 0.30, 1.0);

        // --- two-layer effects -------------------------------------------------
        // The original builds several effects from two impulses with different
        // frequencies, lengths and amplitudes - a sharp front and a long tail. These
        // feed dedicated .Launch/.Tail style channels so ShakeIt can give each layer
        // its own frequency, which is what makes the pairing work.

        /// <summary>Missiles LaunchLength 60ms, attack 50%, decay 50%.</summary>
        [EffectGroup("Missile launch", Section.Weapons)]
        public EffectShape MissileLaunch = new EffectShape(0.06, 0.06, 0.50, 0.50, 1.0, 1.00);

        /// <summary>Missiles TailLength 800ms, attack 5%, decay 20%, amplitude 100%.</summary>
        // Not an [EffectGroup]: identical to Missile above, so the screen showed the
        // same five sliders twice. Weapon.Missile.Tail isn't mapped in ShakeIt either.
        public EffectShape MissileTail = new EffectShape(0.20, 0.80, 0.05, 0.20, 1.0, 1.00);

        /// <summary>PPCs PopLength 120ms, attack 40%, decay 60%.</summary>
        [EffectGroup("PPC pop (discharge)", Section.Weapons)]
        public EffectShape PPCPop = new EffectShape(0.12, 0.12, 0.40, 0.60, 1.0, 1.00);

        /// <summary>PPCs TailLength 700ms, attack 40%, decay 40%, amplitude 50%.</summary>
        [EffectGroup("PPC tail (ringing)", Section.Weapons)]
        public EffectShape PPCTail = new EffectShape(0.20, 0.70, 0.40, 0.40, 1.0, 0.50);

        /// <summary>Melee HitLength 450ms, attack 0%, decay 40%.</summary>
        [EffectGroup("Melee hit", Section.Weapons)]
        public EffectShape MeleeHit = new EffectShape(0.25, 0.45, 0.00, 0.40, 1.0, 1.00);

        /// <summary>Melee SwingLength 1100ms, attack 20%, decay 20%, amplitude 75%.</summary>
        [EffectGroup("Melee swing", Section.Weapons)]
        public EffectShape MeleeSwing = new EffectShape(0.60, 1.10, 0.20, 0.20, 1.0, 0.75);

        /// <summary>PartDestruction Length 700ms, attack 0%, decay 85%.</summary>
        /// <summary>
        /// Physical shock (EventCode 16). Not from the original - Clans reports an
        /// impulse vector, which the original protocol had no equivalent for, so
        /// there's no reference AmplitudeExponent to port (unlike the five Damage
        /// effects). 0.4 is a starting guess in the same spirit: a weak knock that
        /// only just clears ImpulseMinMagnitude was reading as close to nothing on
        /// the linear default (1.0) - this keeps it felt without flattening the
        /// difference from a hard hit the way a very low exponent would.
        /// </summary>
        [EffectGroup("Physical shock (knockback)", Section.Damage)]
        public EffectShape Impulse = new EffectShape(0.10, 0.60, 0.00, 0.30, 0.50) { AmplitudeExponent = 0.4 };

        /// <summary>Impulse magnitude that counts as full strength.</summary>
        [Setting("Impact: full-strength impulse", 100.0, 20000.0, Section.Damage, "Physical shock (knockback)")]
        public double ImpulseMaxMagnitude = 3000.0;

        /// <summary>Ignore shocks below this - walking bumps into scenery constantly.</summary>
        [Setting("Impact: ignore below", 0.0, 2000.0, Section.Damage, "Physical shock (knockback)")]
        public double ImpulseMinMagnitude = 50.0;

        // Not an [EffectGroup]: identical to PartDestruction above.
        public EffectShape PartDestructionPrimary = new EffectShape(0.50, 0.70, 0.00, 0.85, 1.0, 1.00);

        /// <summary>PartDestruction SecondaryLength 1500ms, attack 5%, decay 40%, amplitude 90%.</summary>
        [EffectGroup("Part destruction: 2nd layer", Section.Damage)]
        public EffectShape PartDestructionSecondary = new EffectShape(1.00, 1.50, 0.05, 0.40, 1.0, 0.90);

        /// <summary>
        /// Missiles fired in a stream (MissileInterval > 0) get one impulse per
        /// missile at reduced strength, rather than one big hit. Original: 40%.
        /// </summary>
        [Setting("Missile stream: per-missile strength", 0.05, 1.0, Section.Weapons, "Missile")]
        public double MissileStreamFactor = 0.40;

        /// <summary>Cap on how many individual missile impulses to schedule.</summary>
        [Setting("Missile stream: max missiles", 1.0, 60.0, Section.Weapons, "Missile")]
        public double MissileStreamMax = 40.0;

        // --- sustained effects: fade in / out ---------------------------------
        // The original ramps sustained effects rather than switching them, via
        // TransitionOnTime / TransitionOffTime.

        /// <summary>
        /// Level while the jets are burning. They run for seconds at a time, so full
        /// strength wears quickly - unlike a weapon impulse that is over instantly.
        /// </summary>
        [Setting("Jump jets: strength", 0.0, 1.0, Section.Movement, "Jump jets")]
        public double JumpJetsStrength = 0.65;

        [Setting("Jump jets: fade in", 0.0, 2.0, Section.Movement, "Jump jets")]
        public double JumpJetsFadeIn = 0.03;

        /// <summary>
        /// The original uses 1.5s here, but that was tuned for a spooling-down jet
        /// SOUND. Felt through a shaker it reads as the effect lagging behind the
        /// game - the burn visibly stops while the seat is still humming. Short
        /// enough to feel immediate, long enough not to be an abrupt cut.
        /// </summary>
        [Setting("Jump jets: fade out", 0.0, 4.0, Section.Movement, "Jump jets")]
        public double JumpJetsFadeOut = 0.30;

        [Setting("MASC: fade in", 0.0, 2.0, Section.Movement, "MASC")]
        public double MascFadeIn = 0.05;

        [Setting("MASC: fade out", 0.0, 4.0, Section.Movement, "MASC")]
        public double MascFadeOut = 0.30;

        [Setting("Dropship flight: fade out", 0.0, 5.0, Section.Events, "Dropship")]
        public double DropshipFadeOut = 2.50;

        /// <summary>
        /// Level while MASC is engaged. It stays on for seconds and its job is to say
        /// "the actuators are being overdriven", not to compete with weapons - the
        /// speed itself already comes through as faster footfalls. Kept low.
        ///
        /// Clans only reports MASC through OnMASCEngaged(bool); the gauge-carrying
        /// OnMASCChangeReported never fires, so there is nothing to scale by and this
        /// is a flat level rather than a curve.
        /// </summary>
        [Setting("MASC: strength", 0.0, 1.0, Section.Movement, "MASC")]
        public double MascStrength = 0.30;

        /// <summary>Applied to the gauge if a build ever starts reporting one.</summary>
        [Setting("MASC: gauge curve", 0.1, 2.0, Section.Movement, "MASC")]
        public double MascGaugeExponent = 0.6;

        // --- torso twist -------------------------------------------------------
        // The original scales twist strength by rate-of-change relative to the mech's
        // maximum turn rate. Clans doesn't expose that maximum, so this is the
        // reference rate that counts as "full strength" - tune to taste.

        /// <summary>
        /// Overall level for this channel. Sustained channels have no EffectShape
        /// to carry an Amplitude, so this is where their balance against the
        /// momentary effects sharing the same band gets set.
        /// </summary>
        [Setting("Torso twist: strength", 0.0, 1.0, Section.Movement, "Torso twist")]
        public double TorsoTwistStrength = 1.0;

        [Setting("Torso twist: full-strength rate (deg/s)", 5.0, 200.0, Section.Movement, "Torso twist")]
        public double TorsoTwistMaxRate = 60.0;

        [Setting("Torso twist: mass influence", 0.0, 1.0, Section.Movement, "Torso twist")]
        public double TorsoTwistMassFactor = 0.65;

        [Setting("Torso twist: fade", 0.0, 1.0, Section.Movement, "Torso twist")]
        public double TorsoTwistFade = 0.05;

        /// <summary>Below this the torso counts as still and the channel drops to 0.</summary>
        [Setting("Torso twist: deadzone (deg/s)", 0.0, 20.0, Section.Movement, "Torso twist")]
        public double TorsoTwistDeadzone = 1.0;

        // --- continuous weapons: level ----------------------------------------
        // Held-down weapons drive their channel at a fixed level rather than through
        // an EffectShape, so there is no Amplitude to balance them with. Confirmed
        // in game (2026-08-25): TAG really is a continuous beam, as the original
        // assumed. It runs for as long as the trigger is held, which makes it tiring
        // at full strength.

        [Setting("TAG: strength", 0.0, 1.0)]
        public double TagStrength = 0.45;

        // --- continuous weapons: repeat rate ----------------------------------
        // MachineGuns.cs and AMS.cs don't hold a level - they fire one impulse per
        // shot, spaced by 1/RateOfFire. That per-shot rhythm is most of what a
        // machine gun feels like, so the plugin re-creates it by pulsing the channel.

        [Setting("Machine gun / AMS: pulse per shot", 0.0, 1.0, Section.Weapons, "Machine gun / AMS: repeat pulse")]
        public double RepeatPulseEnabled = 1.0;

        /// <summary>Fraction of each shot interval the pulse stays up.</summary>
        [Setting("Machine gun / AMS: pulse width", 0.05, 1.0, Section.Weapons, "Machine gun / AMS: repeat pulse")]
        public double RepeatPulseWidth = 0.70;

        /// <summary>Safety clamp - absurd rates of fire would become a tone, not a rhythm.</summary>
        [Setting("Machine gun / AMS: max shots/sec", 1.0, 40.0, Section.Weapons, "Machine gun / AMS: repeat pulse")]
        public double RepeatMaxRate = 25.0;

        // --- intensity weighting ---------------------------------------------
        // MechVibe weights part destruction by which part was lost; the same
        // factors are applied here so an arm and a head don't feel identical.

        [Setting("Part destroyed: head", 0.0, 1.0, Section.Damage, "Part destroyed")]
        public double HeadFactor = 1.00;

        [Setting("Part destroyed: center torso", 0.0, 1.0, Section.Damage, "Part destroyed")]
        public double CenterTorsoFactor = 1.00;

        [Setting("Part destroyed: side torso", 0.0, 1.0, Section.Damage, "Part destroyed")]
        public double SideTorsoFactor = 0.85;

        [Setting("Part destroyed: arm", 0.0, 1.0, Section.Damage, "Part destroyed")]
        public double ArmFactor = 0.65;

        [Setting("Part destroyed: leg", 0.0, 1.0, Section.Damage, "Part destroyed")]
        public double LegFactor = 0.75;

        /// <summary>Footsteps: how much mech tonnage drives footstep strength.</summary>
        [Setting("Footstep: mass influence", 0.0, 1.0, Section.Movement, "Footstep")]
        public double FootstepMassFactor = 0.65;

        /// <summary>
        /// Footsteps: how much moving faster softens the footfall (0 = no effect,
        /// 1 = full range). Direction matches the original: a sprint is a light,
        /// quick step, not a heavier one.
        /// </summary>
        [Setting("Footstep: speed influence (faster = softer)", 0.0, 1.0, Section.Movement, "Footstep")]
        public double FootstepSpeedFactor = 0.40;

        /// <summary>Melee: how much tonnage drives melee strength.</summary>
        [Setting("Melee: mass influence", 0.0, 1.0, Section.Weapons, "Melee hit")]
        public double MeleeMassFactor = 0.75;

        public double PartFactorFor(int mechPart)
        {
            switch (mechPart)
            {
                case 0:            return HeadFactor;         // Head
                case 1:            return CenterTorsoFactor;  // CenterTorso
                case 2: case 5:    return SideTorsoFactor;    // Left/RightTorso
                case 3: case 6:    return ArmFactor;          // Left/RightArm
                case 4: case 7:    return LegFactor;          // Left/RightLeg
                default:           return 1.0;
            }
        }
    }

    /// <summary>
    /// Which section of the settings page a field belongs to. The page groups by
    /// this first so related things sit together - a weapon's envelope and the
    /// slider that tunes it were previously in different halves of the page.
    /// The order here is the order they appear.
    /// </summary>
    public enum Section
    {
        Weapons,
        Movement,
        Damage,
        Events
    }

    /// <summary>Labels a plain numeric field for the settings UI.</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class SettingAttribute : Attribute
    {
        public readonly string  Label;
        public readonly double  Min;
        public readonly double  Max;
        public readonly Section Section;

        /// Dropdown this slider is filed under. Matching an [EffectGroup]'s label
        /// puts the slider in that group's dropdown alongside its envelope; any
        /// other string groups it with other [Setting]s sharing that name; null
        /// falls back to the slider's own Label, so every slider ends up in some
        /// dropdown rather than sitting loose on the page.
        public readonly string Group;

        public SettingAttribute(string label, double min, double max,
                                Section section = Section.Weapons, string group = null)
        {
            Label   = label;
            Min     = min;
            Max     = max;
            Section = section;
            Group   = group;
        }
    }

    /// <summary>Labels an EffectShape field, rendered as a collapsible group.</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class EffectGroupAttribute : Attribute
    {
        public readonly string  Label;
        public readonly Section Section;

        public EffectGroupAttribute(string label, Section section = Section.Weapons)
        {
            Label   = label;
            Section = section;
        }
    }
}
