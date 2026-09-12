-- MechVibe: hooks Clans' telemetry events and relays them to MechVibe.
--
-- ARCHITECTURE (option B - no UE4SS rebuild required):
--   this script  --(named pipe, 32-byte packets via string.pack)-->
--   MechVibeBridge.exe  --(shared memory "MechVibeMemory")-->
--   MechVibe.exe
--
-- The original mod did the same job as a UEVR C++ plugin, but a UE4SS C++ mod would
-- need UE4SS core rebuilt from source, which needs a private Epic-org submodule we
-- can't clone. So the packing lives here in Lua and a tiny dependency-free Win32 exe
-- (MechVibeBridge, shipped in this mod's Bridge\ folder) does the shared-memory
-- half. Verified: plain fopen() can open a named pipe, and Lua's io.open is the
-- same CRT call.
--
-- Wire format, byte-identical to the original EventData struct:
--   int32 EventCode, int32 Int0, float Float0..Float5   = 32 bytes
-- See this mod's protocol reference doc for the per-event field mapping.

local UEHelpers = require("UEHelpers")

--------------------------------------------------------------------------------
-- Hot reload
--------------------------------------------------------------------------------
--
-- UE4SS's own Ctrl+R hangs this mod: uninstall() unregisters every hook while the
-- game thread is still firing them, and with torso twist alone running at 20/sec
-- it always is. UE4SS can't be rebuilt (see the header), so reloading is done here
-- instead - and by never touching the hooks, the step that hangs is skipped.
--
-- The trick is that hooks call through this table rather than closing over their
-- callback: re-running the file rebuilds the handlers, and the hooks pick up the
-- new ones on their next call. RegisterCustomEvent could not do this itself - it
-- silently ignores a second registration for the same name (LuaMod.cpp:2359), so
-- re-registering would leave the old code running.
--
-- Only what must outlive a reload lives here. Everything else is a plain local
-- that gets rebuilt, which is what makes edits take effect.
_G.MechVibe = _G.MechVibe or { H = {} }
local MSP = _G.MechVibe

-- Captured on first load so F6 knows what to re-run.
if not MSP.scriptPath then
    local src = debug.getinfo(1, "S").source
    MSP.scriptPath = src:sub(1, 1) == "@" and src:sub(2) or src
end

local VERSION = "1.0.0"

-- Diagnostics: unexpected conditions, plus each distinct weapon name once. Cheap
-- enough to leave on, but off by default in the release build to keep UE4SS.log
-- quiet. Turn on when troubleshooting.
local DEBUG_LOG = false

-- Every single relayed packet. Damage alone runs to thousands of events per fight,
-- so this floods UE4SS.log - the bridge console already shows the same thing with
-- --verbose. Only turn it on to debug the Lua side specifically.
local LOG_EVERY_PACKET = false

local PIPE_PATH = "\\\\.\\pipe\\MechVibeBridge"

-- Launch the bridge automatically if it isn't running, so the game is the only
-- thing that has to be started by hand. The exe guards itself with a named mutex,
-- so a copy started manually (e.g. with --verbose for debugging) wins and this
-- auto-launch just exits.
-- Set false to always start it yourself.
local AUTO_START_BRIDGE = true

-- Ships inside the mod folder (Mods\MechVibe\Bridge\MechVibeBridge.exe), so the
-- path is derived from this script's own location rather than hardcoded - the mod
-- works regardless of where the game is installed. MSP.scriptPath is this file's
-- absolute path, captured below.
local function DefaultBridgeExePath()
    if not MSP.scriptPath then return nil end
    local path, n = MSP.scriptPath:gsub("Scripts\\main%.lua$", "Bridge\\MechVibeBridge.exe")
    return n == 1 and path or nil
end
local BRIDGE_EXE = DefaultBridgeExePath()

-- SimHub shows each custom game with its own Activate/Stop toggle
-- ("MW5:Clans" here), separate from the bridge - even with everything else
-- running, telemetry stays dead until that toggle is on.
--
-- Off by default: the game code below is one specific install's SimHub GUID
-- (PluginsData\CustomGames.json), which will NOT match another user's SimHub
-- unless they used the exact same setup steps. To enable:
--   1. In SimHub, add a custom game named "MW5:Clans" (or import the one from
--      this mod's install guide) and let it run once so SimHub assigns it a
--      Code (a "Custom_<guid>" string) in PluginsData\CustomGames.json.
--   2. Copy that Code into CLANS_GAME_CODE below and flip AUTO_ACTIVATE_SIMHUB
--      to true.
-- -switchgame is SimHub's own CLI (6.8.7+, and works at initial launch too from
-- 9.5.6+): sent to an already-running SimHub it activates that game there; if
-- SimHub isn't running yet, this launches it already switched to it.
local AUTO_ACTIVATE_SIMHUB = false
local SIMHUB_EXE  = [[C:\Program Files (x86)\SimHub\SimHubWPF.exe]]
local CLANS_GAME_CODE = "Custom_a1d8648b-5965-4321-aca6-798d70a10a72"

-- EventCode enum (MechVibeEngine/EventCode.cs).
local EV = {
    ClearFX         = -2,
    BridgeClosed    = -1,
    NULL            = 0,
    TorsoTwist      = 1,
    Footstep        = 2,
    Trace           = 3,
    Projectile      = 4,
    Missiles        = 5,
    AMS             = 6,
    Melee           = 7,
    Dropship        = 8,
    JumpJets        = 9,
    Airborne        = 10,
    Landed          = 11,
    MASC            = 12,
    Powering        = 13,
    PartDestruction = 14,
    Damaged         = 15,

    -- [Clans 확장] 원본에 없는 코드. 물리 충격의 방향과 크기.
    -- Int0   = 방향 (0 Front / 1 Right / 2 Rear / 3 Left / 4 Above / 5 Below)
    -- Float0 = Impulse 크기
    -- Float1 = 전후 성분 (-1~1, 양수 = 정면에서)
    -- Float2 = 좌우 성분 (-1~1, 양수 = 우측에서)
    -- Float3 = 상하 성분 (-1~1, 양수 = 위에서)
    -- Float4 = MinBreakImpulse
    Impulse         = 16,
}

-- GetMechSpeed() returns Unreal's native cm/s. Confirmed against logged values: a
-- 65t mech peaked at 1460, and 1460 * 0.036 = 52.6 km/h, which is a sane top speed.
local CMS_TO_KMH = 0.036

-- Minimum gap between relayed torso-twist events, in seconds. The event itself
-- fires ~5x per tick (~300/sec at 60fps); 20/sec is still smooth to feel and
-- leaves the ring buffer free for everything else.
local TORSO_TWIST_INTERVAL = 0.05

-- Below this the torso counts as still, in deg/s. Measured in game: deliberate
-- aiming runs 20-95, and the slow drift back to centre after a shot runs 1-4.
--
-- Kept low on purpose. The drift is real movement and deserves its (very faint)
-- feedback - strength is proportional to rate, so 4 deg/s comes out at about 7%.
-- A threshold set inside the drift range is the worst of both: the rate crosses
-- it repeatedly and the channel stutters on and off, which is what made a shot
-- feel like it left several aftershocks. Only a fully stopped torso reads 0.00.
local TORSO_TWIST_MIN_RATE = 0.5

local function Log(fmt, ...)
    print(string.format("[MechVibe] " .. fmt .. "\n", ...))
end

Log("Lua v%s loaded (bridge exe: %s)", VERSION, tostring(BRIDGE_EXE))

--------------------------------------------------------------------------------
-- Bridge connection
--------------------------------------------------------------------------------

-- The pipe handle can't survive a script reload (UE4SS tears the Lua state down on
-- every mission restart), so we just reconnect. The helper exe recreates its pipe
-- instance per client, so reconnecting is cheap and expected.
--
-- An F6 reload is different: the Lua state stays alive, so the pipe is kept in MSP
-- and survives. Reconnecting on every edit would drop packets for no reason.
local CONNECT_RETRY_SECONDS = 3
local lastConnectAttempt = 0

local function ClosePipe()
    if MSP.pipe then
        pcall(function() MSP.pipe:close() end)
        MSP.pipe = nil
    end
end

local function TryLaunchBridge()
    if not AUTO_START_BRIDGE or MSP.bridgeLaunchTried then return end
    MSP.bridgeLaunchTried = true

    if not BRIDGE_EXE then
        Log("Bridge not running - launch attempt: FAILED: couldn't derive Bridge exe path from script location (%s)", tostring(MSP.scriptPath))
        return
    end

    -- `start` returns as soon as the child is spawned, so this doesn't block the
    -- game thread waiting for the bridge to exit. /min keeps the console out of
    -- the way; the bridge also mirrors everything into bridge.log next to the exe,
    -- which is what you actually read after a session.
    --
    -- --exclude 15 drops Damaged from the console: it runs to thousands of events
    -- per fight and would bury the weapon lines. It's still relayed and still
    -- counted in the summary printed on disconnect.
    local cmd = string.format('start "MechVibeBridge" /min "%s" --verbose --exclude 15', BRIDGE_EXE)
    local ok, err = pcall(os.execute, cmd)
    Log("Bridge not running - launch attempt: %s", ok and "issued" or ("FAILED: " .. tostring(err)))
end

local function TryActivateSimHub()
    if not AUTO_ACTIVATE_SIMHUB or MSP.simhubActivateTried then return end
    MSP.simhubActivateTried = true

    local cmd = string.format('start "SimHub" /min "%s" -switchgame %s', SIMHUB_EXE, CLANS_GAME_CODE)
    local ok, err = pcall(os.execute, cmd)
    Log("SimHub activate (-switchgame): %s", ok and "issued" or ("FAILED: " .. tostring(err)))
end

local function EnsureConnected()
    if MSP.pipe then return true end

    -- Don't hammer io.open on every single event when the helper isn't running -
    -- in combat that would be hundreds of failed opens per second.
    local now = os.time()
    if now - lastConnectAttempt < CONNECT_RETRY_SECONDS then return false end
    lastConnectAttempt = now

    local ok, handle = pcall(io.open, PIPE_PATH, "wb")
    if not ok or not handle then
        -- Nothing listening yet. Start the bridge, and the next retry (a few
        -- seconds later) will connect to it. SimHub's own Activate toggle is a
        -- separate requirement from the bridge existing - bundled here since
        -- both are "no telemetry flowing yet, go fix the external pieces".
        TryLaunchBridge()
        TryActivateSimHub()
        return false
    end

    -- Unbuffered: haptics are latency-sensitive, and the default 4KB buffer would
    -- hold events back until it filled.
    pcall(function() handle:setvbuf("no") end)
    MSP.pipe = handle
    Log("Connected to MechVibeBridge.")
    return true
end

local function ToInt(v)
    local n = tonumber(v)
    if not n then return 0 end
    return math.floor(n)
end

local function ToNum(v)
    return tonumber(v) or 0.0
end

local function SendEvent(code, int0, f0, f1, f2, f3, f4, f5)
    if not EnsureConnected() then return end

    local packed, packet = pcall(string.pack, "<i4i4ffffff",
        ToInt(code), ToInt(int0),
        ToNum(f0), ToNum(f1), ToNum(f2), ToNum(f3), ToNum(f4), ToNum(f5))

    if not packed then
        Log("string.pack failed for EventCode=%s: %s", tostring(code), tostring(packet))
        return
    end

    local wrote, werr = pcall(function() MSP.pipe:write(packet) end)
    if not wrote then
        -- Helper exited or the pipe broke. Drop the handle so we reconnect later.
        Log("Pipe write failed (%s) - will reconnect.", tostring(werr))
        ClosePipe()
        return
    end

    if LOG_EVERY_PACKET then
        Log("-> code=%d int0=%d f=[%.2f %.2f %.2f %.2f %.2f %.2f]",
            ToInt(code), ToInt(int0), ToNum(f0), ToNum(f1), ToNum(f2), ToNum(f3), ToNum(f4), ToNum(f5))
    end
end

--------------------------------------------------------------------------------
-- Hook-parameter helpers
--------------------------------------------------------------------------------

local function SafeGetFullName(obj)
    local ok, result = pcall(function() return obj:GetFullName() end)
    if ok then return result end
    return "<GetFullName failed: " .. tostring(result) .. ">"
end

-- Full names run to 200+ characters of map path. Keep the last colon-separated
-- segment, which is the part that identifies the object.
local function ShortName(fullName)
    return (tostring(fullName):match("[^:]+$")) or tostring(fullName)
end

-- Hook params arrive wrapped (RemoteUnrealParam/LocalUnrealParam); :get() unwraps to
-- a primitive, a UObject, or a struct wrapper that supports dot-access.
--
-- Values reached by property dot-access are NOT wrapped - they're already the real
-- thing and have no :get(). Returning nil there (as this used to) silently killed
-- every struct/object traversal: it's what made weapon.WeaponComponents.Emitter
-- come back empty and log "no emitter found" on every shot. So a failed :get()
-- means "already unwrapped", not "no value".
local function Unwrap(v)
    if v == nil then return nil end
    local t = type(v)
    if t == "number" or t == "boolean" or t == "string" then return v end
    local ok, val = pcall(function() return v:get() end)
    if ok then return val end
    return v
end

local function Field(structWrapper, name)
    local s = Unwrap(structWrapper)
    if s == nil then return nil end
    local ok, val = pcall(function() return s[name] end)
    if not ok then return nil end
    return Unwrap(val)
end

local function Describe(v)
    local val = Unwrap(v)
    if val == nil then return "nil" end
    local t = type(val)
    if t == "number" or t == "boolean" or t == "string" then return tostring(val) end
    local nok, nameval = pcall(function() return val:GetFullName() end)
    if nok then return nameval end
    return "<unnamed>"
end

--------------------------------------------------------------------------------
-- Mech stats
--------------------------------------------------------------------------------

-- CRITICAL: never call comp.MechDataAsset:GetMechStats() from Lua. UE4SS packs a
-- UFunction's params+return into a fixed 512-byte stack buffer
-- (LuaUFunction.hpp: DynamicUnrealFunctionData::data[0x200]), and FMechStats is
-- 0x298 = 664 bytes -> guaranteed stack-buffer-overrun (0xc0000409). The MechData
-- PROPERTY holds the same data and pure dot-access never touches that buffer.
-- GetMechSpeed() is fine: it returns a single float.
local cachedTons = nil

local function GetTons(comp)
    if cachedTons then return cachedTons end
    local ok, tons = pcall(function()
        return comp.MechDataAsset.MechData.MechDataStats.BaseStats.Tons
    end)
    if ok and tonumber(tons) then
        cachedTons = math.floor(tonumber(tons))
        return cachedTons
    end
    return 0
end

local function GetSpeedKmh(comp)
    local ok, speed = pcall(function() return comp.MechPawn:GetMechSpeed() end)
    if ok and tonumber(speed) then
        return tonumber(speed) * CMS_TO_KMH
    end
    return 0
end

--------------------------------------------------------------------------------
-- Weapon classification
--------------------------------------------------------------------------------

-- Clans' weapon emitter stat structs line up 1:1 with the protocol's float fields,
-- which is effectively the original MechVibeRelay Blueprint's mapping recovered
-- from the other end (SDK dump: FWeaponEmitterStats_Trace/_Projectile/_Missile):
--   Trace      DamageOverDuration/HeatDamageOverDuration/RateOfFire/Duration
--   Projectile NumberOfTimesToFire/DelayBetweenFiring/NumberOfProjectiles/Speed/Impulse
--   Missile    NumberOfMissiles/MissileInterval + nested Projectile.Speed/.Impulse
--
-- The emitter lives at weapon.WeaponComponents.Emitter and its concrete subclass
-- (UMWWeaponEmitter_Trace/_Projectile/_Missile/_AMS/_Melee) decides the event type.
-- We detect the type by which stats property exists rather than by class name,
-- because the live objects are Blueprint-derived and their names aren't reliable.
-- Everything here is property dot-access, so none of it can hit the 512-byte
-- UFunction buffer bug.

local function TryProp(obj, name)
    if obj == nil then return nil end
    local ok, val = pcall(function() return obj[name] end)
    if not ok then return nil end
    return val
end

local function PropNum(structWrapper, name)
    return ToNum(Unwrap(TryProp(structWrapper, name)))
end

-- Maps an emitter class name to a kind. Checked BEFORE the stats-property probe,
-- because probing turned out to be unreliable: a Dire Wolf's LB-X 20 came back as
-- "trace" even though it's a projectile weapon, which means TryProp can succeed for
-- a stats field the emitter subclass doesn't actually have.
local EMITTER_CLASS_KINDS = {
    { pattern = "_TRACE",      kind = "trace",      stats = "TraceStats"      },
    { pattern = "_PROJECTILE", kind = "projectile", stats = "ProjectileStats" },
    { pattern = "_MISSILE",    kind = "missile",    stats = "MissileStats"    },
    { pattern = "_MELEE",      kind = "melee",      stats = "MeleeStats"      },
    { pattern = "_AMS",        kind = "ams",        stats = nil               },
}

-- Returns emitterKind, statsStruct, reason, emitter.
-- `reason` is only meaningful when the kind is nil.
local function ClassifyEmitter(weapon)
    local wc = Unwrap(TryProp(weapon, "WeaponComponents"))
    if wc == nil then return nil, nil, "weapon.WeaponComponents is nil", nil end

    local emitter = Unwrap(TryProp(wc, "Emitter"))
    if emitter == nil then return nil, nil, "WeaponComponents.Emitter is nil", nil end

    -- Preferred path: the concrete subclass name (UMWWeaponEmitter_Trace,
    -- _Projectile, _Missile, _AMS, _Melee) states the type outright.
    local className = string.upper(SafeGetFullName(emitter))
    for _, entry in ipairs(EMITTER_CLASS_KINDS) do
        if string.find(className, entry.pattern, 1, true) then
            local stats = entry.stats and TryProp(emitter, entry.stats) or emitter
            if stats ~= nil then return entry.kind, stats, nil, emitter end
            -- Name says one thing but the stats field is missing - fall through to
            -- the probe rather than sending garbage.
            break
        end
    end

    -- Fallback: probe for a stats property. Kept because Blueprint-derived emitters
    -- might not carry the base class name, but it's second because of the LB-X case.
    local trace = TryProp(emitter, "TraceStats")
    if trace ~= nil then return "trace", trace, nil, emitter end

    local proj = TryProp(emitter, "ProjectileStats")
    if proj ~= nil then return "projectile", proj, nil, emitter end

    local missile = TryProp(emitter, "MissileStats")
    if missile ~= nil then return "missile", missile, nil, emitter end

    local melee = TryProp(emitter, "MeleeStats")
    if melee ~= nil then return "melee", melee, nil, emitter end

    if TryProp(emitter, "MissilesDestroyedPerSecond") ~= nil then
        return "ams", emitter, nil, emitter
    end

    return nil, nil, "unknown emitter class: " .. SafeGetFullName(emitter), emitter
end

-- The engine only uses WeaponId as a dictionary key to track continuous weapons
-- (AMS.cs/MachineGuns.cs/Flamers.cs/TAG.cs), so any value works as long as it's
-- stable per weapon. MechPart + SlotId is unique and is plain property access.
local function GetWeaponId(weapon)
    local part = ToInt(Unwrap(TryProp(weapon, "MechPart")))
    local slot = ToInt(Unwrap(TryProp(weapon, "SlotId")))
    return part * 1000 + slot
end


--------------------------------------------------------------------------------
-- Damage classification
--------------------------------------------------------------------------------

-- DamagedEvent.cs's DamageType enum.
local DMG = { Trace = 0, Projectile = 1, Missile = 2, Melee = 3, Explosion = 4 }

-- FMWDamageInfo carries no damage category - only WeaponAssetId, HeatDamage and two
-- bools (MechWarrior.hpp:3100). But FKelItemDataAssetId nests a FPrimaryAssetId whose
-- PrimaryAssetName is the weapon's asset name, so the category comes from the name.
-- Turns an FName-ish value into a string. UE4SS hands FName properties back either
-- already converted or as an object with :ToString().
local function FNameToString(v)
    if v == nil then return nil end
    if type(v) == "string" then return v end
    local ok, s = pcall(function() return v:ToString() end)
    if ok and type(s) == "string" and s ~= "" then return s end
    return nil
end

local damageNameDiagnosed = false

-- Resolves an FKelItemDataAssetId (MechWarrior.hpp) to the item's asset name. Both
-- FMWDamageInfo.WeaponAssetId and UMWWeaponComponent.WeaponId are this same struct.
local function AssetIdToName(assetId)
    if assetId == nil then return nil end

    -- Preferred: CachedAssetPtr is a real UKelItemDataAsset*, so GetFullName gives
    -- the asset name directly without going through FName conversion at all.
    local cached = Unwrap(TryProp(assetId, "CachedAssetPtr"))
    if cached ~= nil then
        local ok, n = pcall(function() return cached:GetFullName() end)
        if ok and type(n) == "string" and n ~= "" then return n end
    end

    -- Fallback: FPrimaryAssetId.PrimaryAssetName. CachedId is checked too - it's the
    -- resolved copy and may be populated when ID isn't.
    for _, field in ipairs({ "ID", "CachedId" }) do
        local id = Unwrap(TryProp(assetId, field))
        if id ~= nil then
            local s = FNameToString(Unwrap(TryProp(id, "PrimaryAssetName")))
            if s then return s end
        end
    end

    return nil
end

-- The weapon component's own name is the hardpoint slot, not the gun in it: an
-- arm's third energy hardpoint is "..._EH3_Laser_C" whatever is bolted to it.
-- WeaponId is what actually identifies the weapon.
local function GetWeaponItemName(weapon)
    local w = Unwrap(weapon)
    if w == nil then return nil end
    return AssetIdToName(Unwrap(TryProp(w, "WeaponId")))
end

-- Ballistic sub-type, sent in Float4 of the Projectile event.
--
-- The protocol only defined `IsPPC = Float4 == 0`, so 0 keeps that meaning and the
-- rest extend it. A Gauss rifle and a plain autocannon differ only in numbers, and
-- the pulse laser case proved stats alone can't separate weapons that were meant to
-- feel different - the item asset name is the reliable tell.
--
-- PATTERNS ARE PROVISIONAL. Only PPC is confirmed in game (Clan_ERPPC). The rest are
-- guesses at Clans' naming: fire the weapon, read the ProjectileStats log line, and
-- fix the patterns here if something lands in the wrong bucket (F6 applies it).
local PROJ_PPC   = 0
local PROJ_AC    = 1   -- plain autocannon / rifle, and the fallback
local PROJ_LBX   = 2   -- cluster
local PROJ_GAUSS = 3
local PROJ_UAC   = 4   -- double-tap
local PROJ_RAC   = 5   -- rotary

local PROJ_SUBTYPE_NAMES = { [0] = "PPC", [1] = "AC", [2] = "LBX", [3] = "Gauss", [4] = "UAC", [5] = "RAC" }

local function ProjectileSubtype(weapon)
    local name = GetWeaponItemName(weapon)
    if name == nil then
        -- Asset not resolved yet; the component class at least still catches PPC.
        local ok, cls = pcall(function() return weapon:GetClass() end)
        name = (ok and cls) and SafeGetFullName(cls) or ""
    end

    local n = tostring(name):upper()
    if n:find("PPC",   1, true) then return PROJ_PPC   end
    if n:find("GAUSS", 1, true) then return PROJ_GAUSS end
    if n:find("LBX",   1, true) or n:find("LB_X",   1, true) or n:find("LB-X", 1, true) then return PROJ_LBX end
    if n:find("RAC",   1, true) or n:find("ROTARY", 1, true) then return PROJ_RAC end
    if n:find("UAC",   1, true) or n:find("ULTRA",  1, true) then return PROJ_UAC end
    return PROJ_AC
end

local function GetWeaponAssetName(damageInfo)
    local di = Unwrap(damageInfo)
    if di == nil then return nil end

    local assetId = Unwrap(TryProp(di, "WeaponAssetId"))
    if assetId == nil then return nil end

    local name = AssetIdToName(assetId)
    if name then return name end

    -- Report once what's actually reachable, so a broken path is diagnosable
    -- without another round of guessing.
    if DEBUG_LOG and not damageNameDiagnosed then
        damageNameDiagnosed = true
        local id = Unwrap(TryProp(assetId, "ID"))
        Log("Damage name lookup failed. assetId=%s cached=%s ID=%s PrimaryAssetName=%s type=%s",
            type(assetId), type(cached), type(id),
            tostring(id and Unwrap(TryProp(id, "PrimaryAssetName"))),
            tostring(id and Unwrap(TryProp(id, "PrimaryAssetType"))))
    end

    return nil
end

-- Returns damageType, rateOfFire, duration.
--
-- RateOfFire/Duration only matter for Trace damage, where the engine re-derives the
-- sub-type from them exactly like it does for firing: IsMG is Duration==0 && RoF~=0,
-- IsFlamer is Duration==0 && RoF==0 (DamagedEvent.cs). We don't have the real
-- numbers here, so we supply values that land in the right bucket.
--
-- Classification keys off the asset PATH, which states the category outright:
--   /Game/Items/Weapons/Energy/Clan/PulseLaser_Medium/Clan_MediumPulseLaser
--   /Game/Items/Weapons/Missile/Clan/LRM20_Artemis/Clan_LRM20_Artemis
--   /Game/Items/Weapons/Ballistic/Clan/UltraAC5/Clan_UltraAutocannon5
-- That's far steadier than guessing from weapon names.
--
-- But the folder alone isn't enough, because MechVibe's categories are about how
-- the shot FEELS, not what powers it: a PPC sits under Energy yet is a projectile,
-- and a machine gun sits under Ballistic yet is a continuous trace. Those exceptions
-- are checked first.
local function ClassifyDamageByName(name)
    local n = string.upper(name or "")
    if n == "" then return DMG.Explosion, 0, 0 end

    local function has(needle) return string.find(n, needle, 1, true) ~= nil end

    -- Cross-category exceptions, before the folder check.
    if has("FLAMER") then return DMG.Trace, 0, 0 end                    -- IsFlamer
    if has("MACHINEGUN") or has("MACHINE_GUN") or has("_MG") or has("MG_") then
        return DMG.Trace, 10, 0                                          -- IsMG
    end
    if has("PPC") then return DMG.Projectile, 0, 0 end
    if has("TAG") then return DMG.Trace, 0, 1 end

    -- Folder category.
    if has("/WEAPONS/MISSILE/")   then return DMG.Missile, 0, 0 end
    if has("/WEAPONS/BALLISTIC/") then return DMG.Projectile, 0, 0 end
    if has("/WEAPONS/ENERGY/")    then return DMG.Trace, 0, 1 end
    if has("/WEAPONS/MELEE/")     then return DMG.Melee, 0, 0 end

    -- Name fallback, for anything that arrives without a full asset path.
    if has("LASER") or has("PULSE") then return DMG.Trace, 0, 1 end
    if has("LRM") or has("SRM") or has("MRM") or has("ATM") or has("STREAK") or has("MISSILE") then
        return DMG.Missile, 0, 0
    end
    if has("GAUSS") or has("AUTOCANNON") or has("UAC") or has("LBX") or has("RAC")
        or has("AC2") or has("AC5") or has("AC10") or has("AC20") then
        return DMG.Projectile, 0, 0
    end
    if has("MELEE") or has("PUNCH") or has("KICK") or has("HATCHET") then
        return DMG.Melee, 0, 0
    end

    return DMG.Explosion, 0, 0
end

-- UObject identity: UE4SS wrappers for the same object aren't always the same Lua
-- value, so fall back to comparing full names.
local function SameObject(a, b)
    if a == nil or b == nil then return false end
    if a == b then return true end
    local okA, na = pcall(function() return a:GetFullName() end)
    local okB, nb = pcall(function() return b:GetFullName() end)
    return okA and okB and na == nb
end

-- Log each distinct weapon once instead of on every damage event / every shot.
local seenDamageWeapons = {}
local seenFiredWeapons  = {}
local seenTraceStats      = {}
local seenProjectileStats = {}

-- Diagnostic: when the last shot went off, so the torso hook can log in detail for
-- a couple of seconds afterwards. Firing recoils the torso, and the question is
-- whether that recoil is what keeps the twist channel alive after a shot.
local lastFireAt = 0

-- Continuous-fire weapons (flamer/MG/TAG/AMS) need an explicit Active=0 to stop.
-- Lasers don't: they have a real Duration and the engine winds them down itself.
-- Per TraceEvent.cs the sub-type is inferred from the field combination, and every
-- continuous variant has Duration <= 0, so that's the test.
local activeContinuous = {}

-- Melee(7) has no weapon-component reference at its "hit" event (MeleeStrikeHit
-- is a global custom event with only the target actor, not the weapon that swung -
-- unlike WeaponFireReported, which hands over the UMWWeaponComponent directly).
-- But the swing always fires first and the hit follows shortly after for the same
-- swing, so the swing's weaponId is cached here and reused when the hit lands.
local lastMeleeWeaponId = 0

--------------------------------------------------------------------------------
-- Hook installation
--------------------------------------------------------------------------------

-- Deliberately a plain local, NOT ModRef:SetSharedVariable.
--
-- Shared variables live in LuaMod::m_shared_lua_variables, a STATIC member that
-- survives both script reloads and mod hot-reloads. Custom event callbacks do not:
-- a mod restart runs erase_from_container(this, m_custom_event_callbacks) and drops
-- them. Guarding on a shared variable therefore breaks exactly when you need it to
-- work - after a reload the callbacks are gone but the guard still says "already
-- hooked", so F8 refuses to re-register and nothing fires ever again.
--
-- A local resets with the script, so F8 always re-registers after a reload.
-- Re-registering while callbacks are still alive does nothing: RegisterCustomEvent
-- ignores a second registration for the same name (LuaMod.cpp:2359 checks
-- find_function_hook_data first). That is exactly why the handlers below are stored
-- in a table instead of being handed to RegisterCustomEvent directly - see the hot
-- reload note at the top of the file.

-- Builds the handler table. Called on load and again on every F6 reload; the hooks
-- themselves are installed once by InstallHooks and just look up whatever is in
-- MSP.H at call time.
--
-- SafeHook here does NOT register anything - it only files the callback under its
-- event name. The bodies are unchanged from when they were registered directly.
local function DefineHandlers(comp)
    local H = {}

    -- Every callback below only reads hook-param struct FIELDS (the Stack.Locals()
    -- path), never calls object:Function(), so none of them can hit the 512-byte bug.
    local function SafeHook(fnName, callback)
        H[fnName] = callback
    end

    ----------------------------------------------------------------------------
    -- Events whose protocol mapping is fully known - these relay for real.
    ----------------------------------------------------------------------------

    -- Footstep(2): Int0 = MassInTons, Float1 = SpeedInKmh.
    --
    -- Float0 carries which foot landed: 0 = left, 1 = right. That's an extension,
    -- not part of the original protocol - but a safe one: FootstepEvent.cs only ever
    -- reads Int0 and Float1, so MechVibe.exe ignores Float0 entirely. The SimHub
    -- plugin uses it to drive separate left/right channels.
    -- Diagnostic: every logged Footstep packet has come back isRight=0 - the
    -- "right" substring check has never once matched. Rather than guess at the
    -- real string (bone name? numeric side? "R" vs "right"?), log the raw value
    -- so the actual format can be read off instead of assumed again.
    local footValuesSeen = {}

    -- Found the bug the hard way: `foot` is an FString, and FString needs an
    -- explicit :ToString() to become a Lua string (per UE4SS's own docs - "FString
    -- Methods: ToString() - Returns a string that Lua can understand"). Unwrap()
    -- only tries :get(), which doesn't apply here, so it fell through to returning
    -- the FString userdata itself. tostring() on that userdata prints its
    -- type/address ("fstring: 0x...") rather than the text, so the "right"
    -- substring check was comparing against a memory address and could never
    -- match - every single footstep silently classified as left.
    local function FStringToString(v)
        if v == nil then return "" end
        if type(v) == "string" then return v end
        local ok, s = pcall(function() return v:ToString() end)
        if ok and type(s) == "string" then return s end
        return tostring(v)
    end

    SafeHook("Footstep", function(_self, foot)
        local raw  = Unwrap(foot)
        local name = string.lower(FStringToString(raw))

        if DEBUG_LOG and not footValuesSeen[name] then
            footValuesSeen[name] = true
            Log("Footstep raw: type=%s value=%q", type(raw), name)
        end

        local isRight = string.find(name, "right", 1, true) and 1 or 0
        SendEvent(EV.Footstep, GetTons(comp), isRight, GetSpeedKmh(comp), 0, 0, 0, 0)
    end)

    -- Melee(7): Int0 = MassInTons, Float0 = IsHit. MeleeStrikeHit only fires on a
    -- connect, so IsHit is always 1.
    --
    -- Float1 = WeaponId, reused from lastMeleeWeaponId (see its declaration above) -
    -- this hook has no weapon reference of its own, only the target actor.
    SafeHook("MeleeStrikeHit", function(_self, target)
        SendEvent(EV.Melee, GetTons(comp), 1, lastMeleeWeaponId, 0, 0, 0, 0)
    end)

    -- PartDestruction(14): Int0 = part enum. Clans' EMechParts is value-identical to
    -- the protocol's enum (Head=0, CenterTorso=1, LeftTorso=2, LeftArm=3, LeftLeg=4,
    -- RightTorso=5, RightArm=6, RightLeg=7, Invalid=8) - verified in the SDK dump
    -- (MechWarrior_enums.hpp:1228) - so it passes straight through.
    SafeHook("PartDestructionReported", function(_self, mechPart, partDestroyer)
        SendEvent(EV.PartDestruction, ToInt(Unwrap(mechPart)), 0, 0, 0, 0, 0, 0)
    end)

    -- Powering(13): Int0 = 0 Initialising / 1 PoweringUp / 2 ShuttingDown.
    -- Clans' EMechPowerState has six states (MechWarrior_enums.hpp:1241), so unlike
    -- EMechParts this one needs a real mapping. Powered/ShutDown/ColdShutDown are
    -- steady states with no protocol equivalent, so they're dropped.
    local POWER_STATE_MAP = {
        [0] = 0,  -- Intializing (sic, that's the game's spelling)
        [1] = 1,  -- PoweringUp
        [3] = 2,  -- ShuttingDown
    }
    SafeHook("PowerStateChangeReported", function(_self, powerState)
        local mapped = POWER_STATE_MAP[ToInt(Unwrap(powerState))]
        if mapped == nil then return end
        SendEvent(EV.Powering, mapped, 0, 0, 0, 0, 0, 0)
    end)

    -- MASC(12): Int0 = IsEngaged, Float0 = GaugeValue.
    -- MASC(12): Int0 = engaged, Float0 = gauge.
    --
    -- Only ever seen arriving with engaged=0, so the raw value is logged to find out
    -- whether the flag is being misread or the engage half never reports at all.
    -- ControllerFeedbackComponent has a second function, OnMASCEngaged(bool), which
    -- may be the one that fires on activation - both are hooked.
    -- Confirmed in game: only OnMASCEngaged fires, always with a boolean, and the
    -- gauge-carrying OnMASCChangeReported never does. Both stay hooked in case a
    -- patch changes that.
    local lastMascOn = nil

    local function SendMasc(engagedRaw, gauge, source)
        local on = false
        if type(engagedRaw) == "boolean" then
            on = engagedRaw
        elseif type(engagedRaw) == "number" then
            on = engagedRaw ~= 0
        end

        -- The engine reports each change twice; a repeat carries nothing new.
        if on == lastMascOn and (gauge == nil or gauge == 0) then return end
        lastMascOn = on

        if DEBUG_LOG then
            Log("MASC via %s: %s  gauge=%.3f", source, on and "ON" or "off", gauge or 0)
        end

        SendEvent(EV.MASC, on and 1 or 0, gauge or 0, 0, 0, 0, 0, 0)
    end

    SafeHook("OnMASCChangeReported", function(_self, isEngaged, gaugeValue)
        SendMasc(Unwrap(isEngaged), ToNum(Unwrap(gaugeValue)), "ChangeReported")
    end)

    SafeHook("OnMASCEngaged", function(_self, bEngaged)
        SendMasc(Unwrap(bEngaged), nil, "Engaged")
    end)

    -- Dropship(8): Int0 = 0 TurntableStart / 1 TurntableEnd / 2 Flight / 3 Landed.
    -- The signature is DropShipTurntable(FString State) - a string, not a bool
    -- (ControllerFeedbackComponent.hpp:76). The actual string values have never been
    -- observed (the sequence was never triggered in testing), so this guesses from
    -- the text and logs the raw value to confirm on the first real dropship.
    SafeHook("DropShipTurntable", function(_self, state)
        local s = Unwrap(state)
        Log("DropShipTurntable raw state=%q (verify start/end mapping)", tostring(s))
        local text = tostring(s):lower()
        local isEnd = text:find("end") or text:find("stop") or text:find("finish")
        SendEvent(EV.Dropship, isEnd and 1 or 0, 0, 0, 0, 0, 0, 0)
    end)
    SafeHook("DropshipLanding_Flight", function()
        SendEvent(EV.Dropship, 2, 0, 0, 0, 0, 0, 0)
    end)
    SafeHook("DropshipLanding_Stop", function()
        SendEvent(EV.Dropship, 3, 0, 0, 0, 0, 0, 0)
    end)

    -- WeaponFireReported(Weapon: UMWWeaponComponent*, FireResult: EMWWeaponFireResults).
    -- Only Success (0) is an actual shot; every other value is a rejection
    -- (OnCooldown/Jammed/NoAmmo/...) - see EMWWeaponFireResults in the SDK dump.
    SafeHook("WeaponFireReported", function(_self, weapon, fireResult)
        if ToInt(Unwrap(fireResult)) ~= 0 then return end

        local w = Unwrap(weapon)
        if w == nil then return end

        lastFireAt = os.clock()

        local kind, stats, reason, emitter = ClassifyEmitter(w)
        if kind == nil then
            if DEBUG_LOG then Log("WeaponFire: %s", tostring(reason)) end
            return
        end

        local weaponId = GetWeaponId(w)

        -- Once per weapon, not once per shot - an alpha strike would spam otherwise.
        -- The emitter class is included because it's what the classification keys
        -- off; the weapon path alone doesn't say which emitter subclass it uses.
        if DEBUG_LOG and not seenFiredWeapons[weaponId] then
            seenFiredWeapons[weaponId] = true
            Log("WeaponFire: kind=%s id=%d weapon=%s emitter=%s",
                kind, weaponId, ShortName(SafeGetFullName(w)),
                emitter and ShortName(SafeGetFullName(emitter)) or "nil")
        end

        if kind == "trace" then
            local dmg      = PropNum(stats, "DamageOverDuration")
            local heatDmg  = PropNum(stats, "HeatDamageOverDuration")
            local rof      = PropNum(stats, "RateOfFire")
            local duration = PropNum(stats, "Duration")

            -- Trace covers lasers, pulse lasers, MG, flamer, TAG and AMS, and they're
            -- told apart purely by these numbers. Logged once per weapon so the real
            -- values are visible - a pulse laser in particular should differ from a
            -- plain laser here (likely a non-zero RateOfFire alongside a Duration).
            -- A pulse laser fires in bursts rather than one held beam, but its emitter
            -- stats are indistinguishable from a plain laser's (RateOfFire 0 with a
            -- Duration, measured in-game). The component name is the only tell, so
            -- that goes out in Float4 - a field Trace otherwise leaves unused.
            -- The item asset carries the real weapon name; the component only knows
            -- its hardpoint. Falls back to the component path if the asset is not
            -- resolved yet, which at least keeps the log useful.
            local wname = GetWeaponItemName(w) or tostring(SafeGetFullName(w))
            local isPulse = string.find(string.lower(wname), "pulse", 1, true) and 1 or 0

            if DEBUG_LOG and not seenTraceStats[weaponId] then
                seenTraceStats[weaponId] = true
                Log("TraceStats: id=%d pulse=%d dmg=%.2f heat=%.2f rof=%.2f dur=%.2f",
                    weaponId, isPulse, dmg, heatDmg, rof, duration)
                Log("TraceStats: id=%d item=%s", weaponId, wname)
            end

            SendEvent(EV.Trace, weaponId, dmg, heatDmg, rof, duration, isPulse, 1)

            -- Duration <= 0 means flamer/MG/TAG rather than a laser, so it stays on
            -- until something turns it off.
            if duration <= 0 then
                activeContinuous[weaponId] = { code = EV.Trace, f0 = dmg, f1 = heatDmg, f2 = rof, f3 = duration }
            end

        elseif kind == "projectile" then
            local subtype = ProjectileSubtype(w)

            if DEBUG_LOG and not seenProjectileStats[weaponId] then
                seenProjectileStats[weaponId] = true
                Log("ProjectileStats: id=%d type=%s shots=%.0f gap=%.3f rounds=%.0f speed=%.0f impulse=%.0f",
                    weaponId, PROJ_SUBTYPE_NAMES[subtype] or "?",
                    PropNum(stats, "NumberOfTimesToFire"),
                    PropNum(stats, "DelayBetweenFiring"),
                    PropNum(stats, "NumberOfProjectiles"),
                    PropNum(stats, "Speed"),
                    PropNum(stats, "Impulse"))
                Log("ProjectileStats: id=%d item=%s", weaponId, tostring(GetWeaponItemName(w)))
            end

            SendEvent(EV.Projectile, weaponId,
                PropNum(stats, "NumberOfTimesToFire"),
                PropNum(stats, "DelayBetweenFiring"),
                PropNum(stats, "NumberOfProjectiles"),
                PropNum(stats, "Speed"),
                subtype,
                PropNum(stats, "Impulse"))

        elseif kind == "missile" then
            -- Speed and Impulse live on the nested Projectile struct.
            local nested = TryProp(stats, "Projectile")
            SendEvent(EV.Missiles, weaponId,
                PropNum(stats, "NumberOfMissiles"),
                PropNum(stats, "MissileInterval"),
                PropNum(nested, "Speed"),
                PropNum(nested, "Impulse"),
                0, 0)

        elseif kind == "ams" then
            local rof = PropNum(stats, "RateOfFire")
            SendEvent(EV.AMS, weaponId, rof, 0, 0, 0, 0, 1)
            activeContinuous[weaponId] = { code = EV.AMS, f0 = rof }

        elseif kind == "melee" then
            -- The swing, not the connect. Protocol Melee(7) distinguishes them with
            -- Float0 (IsHit): MeleeStrikeHit sends 1, this sends 0. The original
            -- engine gives the swing its own much longer, softer effect.
            --
            -- [Clans 포팅 확장] Float1 = WeaponId. Not part of the original protocol,
            -- but a safe extension like Footstep's Float0 - MeleeStrikeEvent.cs never
            -- reads Float1. weaponId is already in scope here (this branch lives
            -- inside WeaponFireReported), so the SimHub plugin can split the swing
            -- left/right. Cached for the hit event below, which has no weapon of
            -- its own to read.
            lastMeleeWeaponId = weaponId
            SendEvent(EV.Melee, GetTons(comp), 0, weaponId, 0, 0, 0, 0)
        end
    end)

    -- Turns off every continuous weapon. OnFireStopped carries no weapon argument,
    -- so we can't tell which one stopped - killing all of them is the practical
    -- reading, since the player releasing the trigger stops all of them anyway.
    local function StopAllContinuous()
        for weaponId, info in pairs(activeContinuous) do
            if info.code == EV.Trace then
                SendEvent(EV.Trace, weaponId, info.f0, info.f1, info.f2, info.f3, 0, 0)
            else
                SendEvent(EV.AMS, weaponId, info.f0, 0, 0, 0, 0, 0)
            end
        end
        activeContinuous = {}
    end

    SafeHook("OnFireStopped", StopAllContinuous)
    SafeHook("StopFireFeedback", StopAllContinuous)

    ----------------------------------------------------------------------------
    -- Probing: hit-location events on the mech pawn (not the feedback component).
    --
    -- DamageReported gives no hit location, but AMWMech/DerivedMech exposes
    --   OnArmorDamaged(EMechHitSurfaces, FHitPointsChangedParams)
    --   OnPartDamaged(EMechParts, FHitPointsChangedParams)
    -- and EMechHitSurfaces is richer than EMechParts - it has RearCenterTorso(8),
    -- RearLeftTorso(9), RearRightTorso(10), so front/rear is distinguishable.
    -- FHitPointsChangedParams carries DamageTaken plus HitPoints/MaxHitPoints.
    --
    -- Not yet relayed: first confirm these actually fire and what they contain.
    -- Logged once per distinct surface so a fight doesn't flood the log.
    ----------------------------------------------------------------------------
    local seenHitSurfaces = {}
    SafeHook("OnArmorDamaged", function(_self, surface, params)
        if not DEBUG_LOG then return end
        local s = ToInt(Unwrap(surface))
        if seenHitSurfaces[s] then return end
        seenHitSurfaces[s] = true
        Log("OnArmorDamaged: surface=%d dmg=%s hp=%s/%s",
            s,
            tostring(Field(params, "DamageTaken")),
            tostring(Field(params, "HitPoints")),
            tostring(Field(params, "MaxHitPoints")))
    end)

    local seenDamagedParts = {}
    SafeHook("OnPartDamaged", function(_self, mechPart, params)
        if not DEBUG_LOG then return end
        local p = ToInt(Unwrap(mechPart))
        if seenDamagedParts[p] then return end
        seenDamagedParts[p] = true
        Log("OnPartDamaged: part=%d dmg=%s hp=%s/%s",
            p,
            tostring(Field(params, "DamageTaken")),
            tostring(Field(params, "HitPoints")),
            tostring(Field(params, "MaxHitPoints")))
    end)

    ----------------------------------------------------------------------------
    -- Events still in probe mode - they log but don't relay yet.
    ----------------------------------------------------------------------------

    -- Damaged(15): Int0 = DamageType, Float0 = Damage, Float1 = RateOfFire,
    -- Float2 = Duration. Float5 (InstigatorID) is declared in DamagedEvent.cs but
    -- never read by any effect, so it stays 0.
    --
    -- This event fires for damage we DEAL as well as damage we TAKE, so it's filtered
    -- down to hits on our own mech - otherwise every shot that connects with an enemy
    -- would shake the chair.
    local damageSelfCount, damageOtherCount = 0, 0
    SafeHook("DamageReported", function(_self, instigator, hitActor, damage, damageInfo, hitInfo)
        local hit  = Unwrap(hitActor)
        local mine = Unwrap(TryProp(comp, "MechPawn"))

        if not SameObject(hit, mine) then
            -- Log the first few rejects so a broken filter is visible rather than
            -- looking like "damage events just never arrive".
            damageOtherCount = damageOtherCount + 1
            if DEBUG_LOG and damageOtherCount <= 3 then
                Log("Damage on other actor (%s), skipping - ours is %s",
                    Describe(hitActor), tostring(mine and SafeGetFullName(mine) or "nil"))
            end
            return
        end

        damageSelfCount = damageSelfCount + 1

        local name = GetWeaponAssetName(damageInfo)
        local dtype, rof, duration = ClassifyDamageByName(name)

        local key = tostring(name)
        if DEBUG_LOG and not seenDamageWeapons[key] then
            seenDamageWeapons[key] = true
            Log("Damage weapon %q -> type=%d rof=%g dur=%g (verify this mapping)",
                key, dtype, rof, duration)
        end

        SendEvent(EV.Damaged, dtype, ToNum(Unwrap(damage)), rof, duration, 0, 0, 0)
    end)

    -- Impulse(16): physical shock, with direction.
    --
    -- FTakeImpulseParams carries an ImpulseVector - the direction the mech is being
    -- shoved - plus the magnitude. The game itself uses this to pick which gamepad
    -- motors to buzz (SetImpulseMotorsBasedOnHitPart), so it is the game's own idea
    -- of "which side got hit".
    --
    -- The vector is in world space, so it's projected onto the mech's own forward and
    -- right axes here. Note the sign: being hit from the front pushes you backward,
    -- so a hit from the front gives a NEGATIVE forward dot - the signs are flipped
    -- below so that positive means "came from there".
    -- A shove is reported every frame while the force lasts, decaying as it goes:
    -- one knockback produced 110 packets counting down from 377 to 4. That floods
    -- the 128-slot ring and pushes out weapon and footstep events, so only the
    -- leading edge of a shock is forwarded - a rising magnitude, or a fresh one
    -- after things went quiet. The decaying tail carries no new information.
    local IMPULSE_MIN_MAG  = 25.0   -- below this it isn't felt through a shaker
    local IMPULSE_GAP      = 0.20   -- silence this long means a new shock
    local IMPULSE_RISE     = 1.50   -- or this much stronger than the last sample

    local impulseDiagCount = 0
    local lastImpulseSeen  = 0
    local lastImpulseMag   = 0

    local function VecXYZ(v)
        if v == nil then return 0, 0, 0 end
        return ToNum(Unwrap(TryProp(v, "X"))),
               ToNum(Unwrap(TryProp(v, "Y"))),
               ToNum(Unwrap(TryProp(v, "Z")))
    end

    SafeHook("ImpulseReported", function(_self, impulseParams)
        local p = Unwrap(impulseParams)
        if p == nil then return end

        local mag = ToNum(Field(p, "Impulse"))
        if mag < IMPULSE_MIN_MAG then return end

        -- Leading edge only; see the note above.
        local now      = os.clock()
        local wasQuiet = (now - lastImpulseSeen) > IMPULSE_GAP
        local rising   = mag > lastImpulseMag * IMPULSE_RISE
        lastImpulseSeen = now
        lastImpulseMag  = mag
        if not (wasQuiet or rising) then return end

        local ix, iy, iz = VecXYZ(Unwrap(TryProp(p, "ImpulseVector")))
        local len = math.sqrt(ix * ix + iy * iy + iz * iz)
        if len > 0.0001 then
            ix, iy, iz = ix / len, iy / len, iz / len
        end

        -- Mech axes. Falls back to world axes if these calls don't work; both return
        -- a single FVector (24 bytes), well inside UE4SS's 512-byte call budget.
        local fx, fy, fz = 1, 0, 0
        local rx, ry, rz = 0, 1, 0
        local okF, fwd = pcall(function() return comp.MechPawn:GetActorForwardVector() end)
        local okR, rgt = pcall(function() return comp.MechPawn:GetActorRightVector() end)
        if okF and fwd ~= nil then fx, fy, fz = VecXYZ(Unwrap(fwd)) end
        if okR and rgt ~= nil then rx, ry, rz = VecXYZ(Unwrap(rgt)) end

        -- Negated so positive = the impulse came FROM that direction.
        local fromFwd   = -(ix * fx + iy * fy + iz * fz)
        local fromRight = -(ix * rx + iy * ry + iz * rz)
        local fromAbove = -iz

        local dir
        if math.abs(fromFwd) >= math.abs(fromRight) and math.abs(fromFwd) >= math.abs(fromAbove) then
            dir = fromFwd >= 0 and 0 or 2       -- Front / Rear
        elseif math.abs(fromRight) >= math.abs(fromAbove) then
            dir = fromRight >= 0 and 1 or 3     -- Right / Left
        else
            dir = fromAbove >= 0 and 4 or 5     -- Above / Below
        end

        if DEBUG_LOG and impulseDiagCount < 5 then
            impulseDiagCount = impulseDiagCount + 1
            Log("Impulse: mag=%.1f vec=(%.2f,%.2f,%.2f) fwd=%.2f right=%.2f up=%.2f dir=%d axes=%s",
                mag, ix, iy, iz, fromFwd, fromRight, fromAbove, dir,
                (okF and okR) and "mech" or "WORLD-FALLBACK")
        end

        SendEvent(EV.Impulse, dir, mag, fromFwd, fromRight, fromAbove,
                  ToNum(Field(p, "MinBreakImpulse")), 0)
    end)

    ----------------------------------------------------------------------------
    -- Second component: MechAudioLogicComponent, on the mech pawn rather than the
    -- PlayerController. Owns torso twist, jump jets and landing.
    --
    -- EVERY mech has one of these and the hooks match by function name, so all of
    -- them arrive here - the player's and every AI's. Without the IsOurs() check
    -- below, an enemy tracking a target across the arena drove our shaker, and
    -- since several mechs' angles landed in the same lastYaw the differences came
    -- out as nonsense (95 deg/s while standing perfectly still).
    --
    -- The PlayerController hooks don't need this: there is only one of those in a
    -- single-player game, and DamageReported already filters by hit actor.
    ----------------------------------------------------------------------------
    -- Just a startup log, not a gate: IsOurs()/CurrentAudioComponent() below
    -- already re-read comp.MechPawn.MechAudioComponent live on every call, with
    -- their own self-healing cache, specifically so this doesn't need to be
    -- valid yet. It used to be a gate - `return H` here, skipping every hook
    -- from this point on (torso twist, jump jets, landed) - which was invisible
    -- for as long as HookFeedbackComponent only ever ran after a human was
    -- already sitting in a fully-possessed mech. The 20-36 auto-trigger runs
    -- far earlier (as soon as the PlayerController exists), routinely catching
    -- MechPawn still nil - and the gate then permanently dropped torso/jets/
    -- landed for the whole mission, since nothing calls DefineHandlers again
    -- once HookFeedbackComponent has already reported success.
    local okAudioComp, audioComp = pcall(function() return comp.MechPawn.MechAudioComponent end)
    if okAudioComp and audioComp and audioComp:IsValid() then
        Log("MechAudioComponent: %s", SafeGetFullName(audioComp))
    else
        Log("comp.MechPawn.MechAudioComponent not ready yet (%s) - registering hooks anyway, " ..
            "IsOurs() will self-heal once the mech is possessed.", tostring(audioComp))
    end

    -- IsOurs used to compare against a SNAPSHOT of audioComp taken once here. That
    -- broke the moment the player's mech changed without a fresh F8: a new mission
    -- swaps in a new PlayerController (comp itself goes stale, fixed by the
    -- LoadMap-triggered auto-refresh below), but a mid-mission mech swap keeps the
    -- same PlayerController and just changes comp.MechPawn - the snapshot never
    -- saw that, so the new mech's OWN torso/jump-jet/landed events read as
    -- "someone else's" and got silently filtered out. No vibration until F8.
    --
    -- Fix: re-read comp.MechPawn.MechAudioComponent live on every call instead of
    -- caching it. `comp` itself stays valid across a mid-mission swap (it hangs
    -- off the PlayerController, not the pawn), so this always reflects whichever
    -- mech is current. Address comparison is tried first since GetFullName on
    -- every call (this fires ~20/sec per mech in the arena) would be too costly.
    -- Re-resolving on every call was correct but wasteful: this can run into the
    -- thousands/sec (raw OnTorsoTwist, not the debounced rate, across every mech in
    -- the arena). A mech swap doesn't happen more than once every few seconds at
    -- most, so a short TTL cache cuts the property-chain walk by >95% while still
    -- self-healing well within the time it'd take anyone to notice.
    local CACHE_TTL = 0.5
    local cachedAudioComp = nil
    local cachedAt = 0

    local function CurrentAudioComponent()
        local now = os.clock()
        if cachedAudioComp ~= nil and (now - cachedAt) < CACHE_TTL then
            return cachedAudioComp
        end
        local ok, ac = pcall(function() return comp.MechPawn.MechAudioComponent end)
        cachedAt = now
        cachedAudioComp = (ok and ac ~= nil) and ac or nil
        return cachedAudioComp
    end

    local function IsOurs(selfObj)
        local s = Unwrap(selfObj)
        if s == nil then return false end

        local ac = CurrentAudioComponent()
        if ac == nil then return false end
        if s == ac then return true end

        local okA, sAddr = pcall(function() return s:GetAddress() end)
        local okB, acAddr = pcall(function() return ac:GetAddress() end)
        if okA and okB then return sAddr == acAddr end

        return SafeGetFullName(s) == SafeGetFullName(ac)
    end

    -- JumpJets(9): Int0 = Active.
    --
    -- UMWJumpJetComponent reports the real state, and only Active means the jets
    -- are actually burning (MechWarrior_enums.hpp:534):
    --     0 Inactive   1 Warmup   2 Active   3 Cooldown
    --
    -- This is the reliable source. MechAudioComponent's JumpJetEvent/StopEvent pair
    -- up when the jets are tapped, but holding them until the fuel runs out sent
    -- 1,1 and never a stop - latching the channel on for good. Those two are kept
    -- only as a fallback for if the state hook never fires.
    -- Same live-read fix as IsOurs above - comp.MechPawn is read fresh rather than
    -- snapshotted, so a mid-mission mech swap doesn't leave this pointing at the
    -- mech that used to be current.
    local function IsOurJetComp(selfObj)
        local s = Unwrap(selfObj)
        if s == nil then return false end
        local okPawn, pawn = pcall(function() return comp.MechPawn end)
        if not okPawn or pawn == nil then return false end
        -- GetOwner returns a pointer, so it's well inside the 512-byte call limit.
        local ok, owner = pcall(function() return s:GetOwner() end)
        if not ok or owner == nil then return false end
        return SameObject(Unwrap(owner), pawn)
    end

    -- Jets are driven by polling EJumpJetState, not by a hook.
    --
    -- Three hook attempts all failed. JumpJetStateChange is a delegate SIGNATURE
    -- (FMWJumpJetComponentOnJumpJetStateChange), not a function the engine calls -
    -- registering it succeeds and achieves nothing. Start/ShutdownJumpJets and their
    -- On* spellings are real methods on UMWJumpJetComponent and never fired either,
    -- so the component simply isn't reachable through custom events.
    --
    -- Reading the property has none of those problems; it just needs a clock.
    -- JumpJetEvent is a reliable START signal (only the stop half goes missing), so
    -- polling begins there and stops itself once the jets go Inactive - nothing
    -- ticks while the mech is walking around.
    local JET_POLL_MS   = 40
    local jetComp       = nil
    local jetPolling    = false
    local lastJetActive = nil

    local function PollJetState()
        if not jetPolling or jetComp == nil then return end

        local st     = ToInt(Unwrap(TryProp(jetComp, "JumpJetState")))
        local active = (st == 2) and 1 or 0   -- 2 = Active, the only burning state

        if active ~= lastJetActive then
            lastJetActive = active
            SendEvent(EV.JumpJets, active, 0, 0, 0, 0, 0, 0)
        end

        -- Warmup and Cooldown still lead somewhere; Inactive is the end of it.
        if st == 0 then
            jetPolling = false
            return
        end
        ExecuteWithDelay(JET_POLL_MS, PollJetState)
    end

    local function StartJetPolling()
        -- Re-resolved every call, not cached past the first success: this only
        -- runs once per jump-jet trigger pull, so the cost is trivial, and caching
        -- it permanently was the same staleness bug as IsOurs/IsOurJetComp above -
        -- after a mid-mission mech swap it would keep polling the OLD mech's jets.
        local okJet, c = pcall(function()
            return comp.MechPawn.MechAudioComponent.JumpJetComponent
        end)
        if not okJet or c == nil then
            Log("JumpJetComponent unreachable (%s) - falling back to events.", tostring(c))
            return false
        end
        if jetComp ~= c then
            jetComp = c
            Log("JumpJetComponent: %s", SafeGetFullName(jetComp))
        end

        if not jetPolling then
            jetPolling    = true
            lastJetActive = nil
            PollJetState()
        end
        return true
    end

    SafeHook("JumpJetEvent", function(_self)
        if not IsOurs(_self) then return end

        -- Polling owns the channel once it's running; only fall back to the raw
        -- event if the component can't be reached.
        if StartJetPolling() then return end
        SendEvent(EV.JumpJets, 1, 0, 0, 0, 0, 0, 0)
    end)
    SafeHook("JumpJetStopEvent", function(_self)
        if jetPolling then return end
        if not IsOurs(_self) then return end
        SendEvent(EV.JumpJets, 0, 0, 0, 0, 0, 0, 0)
    end)

    -- Landed(11): Int0 = MassInTons, Float0 = AccelerationInKmh2.
    -- ImpactAcceleration is an FVector in Unreal units (cm/s^2). The exact scale the
    -- original engine expected isn't documented anywhere we have, so this logs the
    -- raw magnitude alongside what it sends - compare against a real landing and
    -- adjust the factor if the shake feels wrong.
    SafeHook("OnLandedCollision", function(_self, hit, impactAcceleration, impactVelocity)
        if not IsOurs(_self) then return end
        local a = Unwrap(impactAcceleration)
        local mag = 0
        if a then
            local okv, v = pcall(function()
                return math.sqrt((a.X or 0) ^ 2 + (a.Y or 0) ^ 2 + (a.Z or 0) ^ 2)
            end)
            if okv then mag = v end
        end
        if DEBUG_LOG then
            Log("OnLandedCollision: |ImpactAcceleration|=%.1f (cm/s^2, verify scale)", mag)
        end
        SendEvent(EV.Landed, GetTons(comp), mag * CMS_TO_KMH, 0, 0, 0, 0, 0)
    end)

    -- TorsoTwist(1): Int0 = MassInTons, Float1 = SpeedInKmh,
    --                Float2/3 = yaw/pitch angular velocity, Float4/5 = yaw/pitch
    --
    -- Fires ~5x per game tick, so it MUST be rate-limited: at 60fps that's 300
    -- events/sec, which would refill the 128-slot ring buffer every 0.4s and push
    -- out every weapon and damage event. Debounced to TORSO_TWIST_INTERVAL.
    --
    -- os.clock() is wall-clock time on Windows (MSVC defines it as elapsed time
    -- since process start), which is what we want - not CPU time.
    --
    -- PROTOCOL NOTE: the original put MaxYawRate/MaxPitchRate in Float2/Float3 and
    -- expected the reader to difference Yaw/Pitch itself. Here Float2/Float3 carry
    -- the ACTUAL angular rates in deg/s, and the SimHub plugin normalises them
    -- against a configurable reference rate.
    --
    -- FTorsoTwistFrame exposes YawVelocity/PitchVelocity, but measured against the
    -- angles they don't line up: over a 50ms window Yaw moved 10.28 -> 13.66 (about
    -- 67 deg/s) while YawVelocity read 0.060. Whatever unit that is, it isn't deg/s,
    -- so the rate is differenced from the angles instead - the same thing the
    -- original TorsoTwist.cs does.
    local lastTwistAt = 0
    local lastYaw, lastPitch = 0, 0
    -- The consumer holds the last level until a new one arrives, so coming to a
    -- stop has to be announced. Without this the shaker keeps buzzing forever.
    local twistActive = false
    local lastTwistLogAt = 0

    SafeHook("OnTorsoTwist", function(_self, torso, leftArm, rightArm)
        -- Before the debounce: an enemy's event would otherwise consume our slot.
        if not IsOurs(_self) then return end

        local now = os.clock()
        local dt  = now - lastTwistAt
        if dt < TORSO_TWIST_INTERVAL then return end

        local yaw   = ToNum(Field(torso, "Yaw"))
        local pitch = ToNum(Field(torso, "Pitch"))

        local yawRate, pitchRate = 0, 0

        -- Skip the very first sample, and any gap long enough that the mech could
        -- have swung anywhere in between (mission load, reconnect).
        if lastTwistAt > 0 and dt > 0 and dt < 0.5 then
            local dYaw = yaw - lastYaw
            -- Guard against wrap-around if the angle is ever reported as -180..180.
            if dYaw >  180 then dYaw = dYaw - 360 end
            if dYaw < -180 then dYaw = dYaw + 360 end

            yawRate   = math.abs(dYaw) / dt
            pitchRate = math.abs(pitch - lastPitch) / dt
        end

        lastTwistAt = now
        lastYaw     = yaw
        lastPitch   = pitch

        -- Diagnostic, once a second. Kept while the deadzone is being tuned - the
        -- rate is the only way to tell deliberate aiming from the post-shot drift.
        if DEBUG_LOG and now - lastTwistLogAt > 1.0 then
            lastTwistLogAt = now
            Log("TorsoTwist: yaw=%.2f pitch=%.2f  yawRate=%.2f pitchRate=%.2f  sending=%s",
                yaw, pitch, yawRate, pitchRate,
                (yawRate >= TORSO_TWIST_MIN_RATE or pitchRate >= TORSO_TWIST_MIN_RATE) and "YES" or "no")
        end

        -- Nothing to feel when the torso is still. Send one zero to close out a
        -- movement that just ended, then stay quiet until it starts again.
        if yawRate < TORSO_TWIST_MIN_RATE and pitchRate < TORSO_TWIST_MIN_RATE then
            if twistActive then
                twistActive = false
                SendEvent(EV.TorsoTwist, GetTons(comp), 0, GetSpeedKmh(comp),
                          0, 0, yaw, pitch)
            end
            return
        end
        twistActive = true

        SendEvent(EV.TorsoTwist, GetTons(comp),
                  0,                     -- Float0: DeltaSeconds (unused)
                  GetSpeedKmh(comp),     -- Float1
                  yawRate, pitchRate,    -- Float2/3: deg/s, differenced from angles
                  yaw, pitch)            -- Float4/5
    end)

    return H
end

--------------------------------------------------------------------------------
-- Hook installation
--------------------------------------------------------------------------------

-- Installs one hook per event, once per game session. Each hook looks the handler
-- up at call time, so an F6 reload swaps the behaviour without re-registering -
-- which is what keeps the hang out of the picture, since nothing ever unhooks.
-- Tracked per event name rather than with a single flag: DefineHandlers returns
-- early if the mech's audio component isn't reachable yet (F8 pressed before a
-- mech exists), so the torso/jets handlers can appear on a later call. A single
-- "installed" flag would lock those out forever.
local function InstallHooks()
    MSP.hooked = MSP.hooked or {}

    local added, failed = 0, 0
    for name in pairs(MSP.H) do
        if not MSP.hooked[name] then
            local eventName = name
            local ok, err = pcall(function()
                RegisterCustomEvent(eventName, function(...)
                    local handler = MSP.H[eventName]
                    if handler then return handler(...) end
                end)
            end)
            if ok then
                MSP.hooked[eventName] = true
                added = added + 1
            else
                failed = failed + 1
                Log("RegisterCustomEvent(%s) FAILED: %s", eventName, tostring(err))
            end
        end
    end

    local total = 0
    for _ in pairs(MSP.hooked) do total = total + 1 end
    if added > 0 or failed > 0 then
        Log("Hooks: %d newly installed, %d failed, %d active.", added, failed, total)
    else
        Log("Hooks: %d already active, handlers refreshed.", total)
    end
end

-- Finds the components, (re)builds the handlers and installs the hooks if needed.
-- Safe to call repeatedly: F8 on a new mission just refreshes the captured
-- component references.
local function HookFeedbackComponent()
    local ok, pc = pcall(UEHelpers.GetPlayerController)
    if not ok or not pc or not pc:IsValid() then
        Log("No valid PlayerController yet - are you in a mission?")
        return false
    end

    local okComp, comp = pcall(function() return pc.ControllerFeedbackComponent end)
    if not okComp or not comp or not comp:IsValid() then
        Log("pc.ControllerFeedbackComponent failed: %s", tostring(comp))
        return false
    end
    Log("ControllerFeedbackComponent: %s", SafeGetFullName(comp))

    MSP.comp = comp
    MSP.H    = DefineHandlers(comp)
    InstallHooks()
    return true
end

--------------------------------------------------------------------------------
-- Keybinds
--------------------------------------------------------------------------------

local function DumpPlayerControllerInfo()
    local ok, pc = pcall(UEHelpers.GetPlayerController)
    if not ok or not pc or not pc:IsValid() then
        Log("No valid PlayerController yet - are you in a mission?")
        return
    end
    Log("PlayerController: %s", SafeGetFullName(pc))
    Log("Bridge pipe: %s", MSP.pipe and "connected" or "NOT connected")

    local handlers, hooks = 0, 0
    if MSP.H      then for _ in pairs(MSP.H)      do handlers = handlers + 1 end end
    if MSP.hooked then for _ in pairs(MSP.hooked) do hooks    = hooks + 1    end end
    Log("Handlers: %d   Hooks active: %d", handlers, hooks)
end

-- Diagnostic: log every level transition. A fatal crash (2026-08-26, engine
-- ACCESS_VIOLATION, cause unconfirmed) happened ~45s after the last logged
-- gameplay event, with CheatManagerEnabler re-constructing in between - which
-- normally means a new level/world was spawned. This turns that guess into a
-- timestamped fact: if a real crash follows a "LoadMap Pre" with no matching
-- "Post", the transition itself is implicated rather than whatever was firing
-- at the time. Not RegisterCustomEvent, so it doesn't dedupe on its own - guard
-- with the same one-time flag pattern as the keybinds below.
if not MSP.levelHooksDone then
    MSP.levelHooksDone = true

    RegisterLoadMapPreHook(function(_engine, _worldContext, url)
        local u = ""
        pcall(function() u = tostring(Unwrap(url)) end)
        Log("LoadMap PRE: url=%s", u)
    end)
    -- A new mission (or a mech swap that reloads the arena) spawns a fresh
    -- PlayerController and MechAudioComponent, but IsOurs()/IsOurJetComp() were
    -- still comparing against the ones captured the last time F8 ran - so the new
    -- mech's own torso/jump jet/landed events read as "someone else's" and got
    -- filtered out. Symptom: no torso vibration after starting a mission or
    -- swapping mechs, until F8 is pressed by hand. Auto-refresh here instead.
    --
    -- The controller/pawn may not be possessed yet the instant LoadMap returns, so
    -- this retries on a short timer rather than assuming one attempt is enough.
    -- 6 attempts (2.5s) was plenty for a mission-to-mission transition inside an
    -- already-running game (the original 20-26 case), but nowhere near enough
    -- for the fresh-boot-straight-into-a-mission case added in 20-36: the whole
    -- game is still starting up, and the PlayerController can easily take much
    -- longer than 2.5s to become valid. 60 attempts (30s) was tried first and
    -- still wasn't enough - logs from an actual cold boot (20-41) showed
    -- pc.ControllerFeedbackComponent returning a bogus "TrivialObject" (hangar/
    -- loadout screen, presumably) for 26 of those seconds, then the real mission
    -- component only resolved 3+ minutes after launch - well past any fixed
    -- 30s-scale budget, because that time is however long the player spends
    -- navigating menus before dropping into the mission, which has no upper
    -- bound. Each attempt is one cheap property check, so there's no real cost
    -- to a much longer budget - 600 attempts (5 minutes) here; still gives up
    -- eventually rather than polling forever if genuinely stuck at a menu.
    local refreshAttempt = 0
    local function RefreshAfterLevelLoad()
        refreshAttempt = refreshAttempt + 1
        if HookFeedbackComponent() then
            Log("Auto-refresh after level load: OK (attempt %d)", refreshAttempt)
            return
        end
        if refreshAttempt < 600 then
            ExecuteWithDelay(500, RefreshAfterLevelLoad)
        else
            Log("Auto-refresh after level load: gave up after %d attempts " ..
                "(probably not in a mission - e.g. main menu)", refreshAttempt)
        end
    end

    RegisterLoadMapPostHook(function(_engine, _worldContext, url)
        local u = ""
        pcall(function() u = tostring(Unwrap(url)) end)
        Log("LoadMap POST: url=%s", u)

        refreshAttempt = 0
        ExecuteWithDelay(500, RefreshAfterLevelLoad)
    end)

    -- A real trigger for the cold-boot-straight-into-a-mission gap, instead of
    -- blindly polling from the moment the script loads: AMWPlayerController's
    -- OnPossessesPawn delegate fires when the controller takes control of its
    -- mech (SDK dump: MechWarrior.hpp, AMWPlayerController). In practice it
    -- never fired in testing - the possess for a mission the game boots
    -- straight into happens too early, before this script finishes loading
    -- and gets the registration in, so there's nothing left here to catch by
    -- the time we're listening. Left in anyway (harmless, and might catch a
    -- later possess - a mech swap mid-mission, say) as a faster path when it
    -- does fire, but it can't be the only mechanism.
    local ok, err = pcall(function()
        RegisterCustomEvent("OnPossessesPawn", function(_self, pawn)
            Log("OnPossessesPawn fired - refreshing hooks.")
            HookFeedbackComponent()
        end)
    end)
    if not ok then
        Log("RegisterCustomEvent(OnPossessesPawn) FAILED: %s", tostring(err))
    end

    -- 20-43: "Restart mission" from the in-game menu doesn't fire LoadMap at
    -- all (it resets actors in place, no travel) and torso twist went silent
    -- again after one - 3 minutes of total log silence post-restart proved
    -- neither LoadMap POST nor OnPossessesPawn ever fired for it. Found the
    -- real trigger by reading CheatManagerEnablerMod's own source (a
    -- different UE4SS mod, but it needs the same "possession re-established"
    -- moment): it hooks the native APlayerController::ClientRestart function
    -- directly via RegisterHook, which fires on every (re)possession -
    -- initial mission start, mid-mission mech swap, AND a menu restart alike.
    -- RegisterHook is UE4SS's own native-function hook (distinct from
    -- RegisterCustomEvent's Blueprint-custom-event matching, and not the same
    -- 512-byte-buffer risk as us calling a UFunction ourselves - we're only
    -- receiving already-marshalled params here, same as every other hook in
    -- this file).
    local okRestart, errRestart = pcall(function()
        RegisterHook("/Script/Engine.PlayerController:ClientRestart", function(_self, _newPawn)
            Log("ClientRestart fired - refreshing hooks.")
            HookFeedbackComponent()
        end)
    end)
    if not okRestart then
        Log("RegisterHook(ClientRestart) FAILED: %s", tostring(errRestart))
    end

    -- The reliable fallback: poll from the moment the script loads. Confirmed
    -- to actually work (20-36) once given enough budget - the cold-boot
    -- PlayerController can take much longer than the original 2.5s to become
    -- valid, hence 60 attempts (30s) rather than 6. This is a direct function
    -- call with no keypress involved, so it doesn't touch the UE4SS keybind
    -- dispatch cost from 20-35 either.
    RefreshAfterLevelLoad()
end

-- Keybinds are registered once per game session. A reload re-runs this file, and
-- registering again would stack duplicate binds - one keypress would fire the
-- handler several times.
if not MSP.keybindsDone then
    MSP.keybindsDone = true

    -- Not F8/F11/etc: whichever key installs hooks first each session costs a
    -- one-time few-second stall. Timing (InstallHooks itself: <20ms) and a
    -- heartbeat ping (zero gaps throughout) both cleared this mod's own code -
    -- the same stall reproduces on F7 alone (nothing but a log line and one
    -- SendEvent) as the session's first keybind press, so it's UE4SS's own
    -- keybind dispatch paying some one-time setup cost, not anything here.
    -- Costs nothing to work around: press any bound key once at the main menu
    -- before loading into a mission, and it's already paid for by the time
    -- Pause needs to install hooks mid-mission.
    RegisterKeyBind(Key.PAUSE, function() HookFeedbackComponent() end)
    RegisterKeyBind(Key.F10, function() DumpPlayerControllerInfo() end)

    RegisterKeyBind(Key.F9, function()
        Log("F9 pressed: generating SDK, this may take a moment...")
        local ok, err = pcall(GenerateSDK)
        Log("GenerateSDK(): %s%s", ok and "done" or "FAILED", ok and "" or (" - " .. tostring(err)))
    end)

    -- F7: fire one synthetic Footstep so the whole chain can be verified without
    -- needing to be in a mission.
    RegisterKeyBind(Key.F7, function()
        Log("F7: sending test Footstep(55t, 40km/h)...")
        SendEvent(EV.Footstep, 55, 0, 40.0, 0, 0, 0, 0)
        Log("Bridge pipe: %s", MSP.pipe and "connected" or "NOT connected")
    end)

    -- F6: re-run this file. Hooks, pipe and component references are kept, so
    -- edits take effect without restarting the game and without the unhook step
    -- that makes UE4SS's own Ctrl+R hang.
    RegisterKeyBind(Key.F6, function()
        Log("F6: reloading %s", tostring(MSP.scriptPath))
        local ok, err = pcall(dofile, MSP.scriptPath)
        if ok then
            Log("F6: reload OK.")
        else
            -- The old handlers are still in place, so a syntax error costs nothing
            -- but the edit - fix it and press F6 again.
            Log("F6: reload FAILED - previous handlers still active. %s", tostring(err))
        end
    end)
end

-- (Removed: the ClientRestart probe hook. It existed only to prove engine UFunction
-- hooking worked, which is long settled, and unlike RegisterCustomEvent, RegisterHook
-- does NOT dedupe - so it piled up a fresh hook id on every script reload.)

-- On an F6 reload the component is already known, so rebuild the handlers straight
-- away rather than making the user press F8 again. InstallHooks runs too, in case
-- the edit added an event that wasn't hooked before.
if MSP.comp then
    local okRebuild, err = pcall(function()
        MSP.H = DefineHandlers(MSP.comp)
        InstallHooks()
    end)
    if okRebuild then
        Log("Reloaded - handlers rebuilt.")
    else
        Log("Reload FAILED, previous handlers still active: %s", tostring(err))
    end
else
    Log("Loaded. Pause/Break = install hooks, F6 = reload script, F7 = test packet, F9 = SDK dump, F10 = status.")
end
