using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NomadDriveTow
{
    /// <summary>
    /// EXPERIMENT (host only). Press the key near two vehicles: the heavier one tows the other.
    /// A physics joint is created on the host between the carrier's rear (Motorcycle Holder position)
    /// and the towed vehicle's front Tow Knob slot. Press again to release.
    ///
    /// v0.2: the game's NetworkedNWHVehicle holds driverless vehicles still (ServerHoldDriverlessVehicle /
    /// DampDriverlessMotion pull the velocity to zero at 20 m/s^2) and can put their wheel simulation
    /// to sleep (ApplyWheelSimulationGate). Both are skipped for the towed vehicle while it is attached.
    /// </summary>
    [BepInPlugin("com.nunod87.nomaddrive.tow", "Nomad Drive Tow (preview)", "0.5.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin I;

        private ConfigEntry<string> _key;
        private ConfigEntry<float> _maxDistance;
        private ConfigEntry<bool> _requireHeavier;
        private ConfigEntry<bool> _requireSameDirection;
        private ConfigEntry<float> _breakForce;
        private ConfigEntry<float> _pitchRollLimit;
        private ConfigEntry<float> _yawLimit;
        private ConfigEntry<bool> _skipDriverlessHold;
        private ConfigEntry<bool> _forceWheels;
        private ConfigEntry<bool> _takeAuthority;
        private ConfigEntry<bool> _prepareTowed, _blockDriver, _allowSteer;
        private ConfigEntry<float> _autoRelease;

        private Type _vehicleManager, _networkServer, _networkedNwh;
        private bool _patched;

        private ConfigurableJoint _joint;
        private Rigidbody _carrierRb, _towedRb;
        private Component _towedVm;
        private string _carrierName, _towedName;
        private float _carrierSleep, _towedSleep;
        private float _nextDiag;
        private Vector3 _lastCarrierPos, _lastTowedPos;
        private string _toast;
        private float _toastUntil;

        private void Awake()
        {
            I = this;
            _key = Config.Bind("General", "TowKey", "F2", "Key that attaches/releases the tow (new Input System key name).");
            _maxDistance = Config.Bind("General", "MaxAttachDistance", 8f, "Max gap (m) between the carrier's rear and the towed vehicle's tow knob to attach.");
            _requireHeavier = Config.Bind("General", "CarrierMustBeHeavier", true, "Only let the heavier (or equal) vehicle tow the lighter one.");
            _requireSameDirection = Config.Bind("General", "RequireSameDirection", true, "Both vehicles must face roughly the same way.");
            _breakForce = Config.Bind("Joint", "BreakForce", 0f, "Force that snaps the tow. 0 = unbreakable.");
            _pitchRollLimit = Config.Bind("Joint", "PitchRollLimitDegrees", 20f, "How far the towed vehicle may pitch/roll against the carrier.");
            _yawLimit = Config.Bind("Joint", "YawLimitDegrees", 80f, "How far the towed vehicle may swing sideways (yaw).");
            _skipDriverlessHold = Config.Bind("Fixes", "SkipDriverlessHold", true, "While towed, skip the game's driverless-vehicle hold/damping.");
            _forceWheels = Config.Bind("Fixes", "ForceWheelSimulation", true, "While towed, keep the wheel simulation awake.");
            _takeAuthority = Config.Bind("Fixes", "HostTakesAuthority", true, "On attach, give the towed vehicle's network ownership to the host so the host simulates it (needed if a client drove it last).");
            _prepareTowed = Config.Bind("Fixes", "PrepareTowedVehicle", true, "On attach: turn off the towed vehicle's idle/disabled braking and put it in neutral (restored on release).");
            _blockDriver = Config.Bind("Fixes", "BlockTowedDriverControls", true, "While towed, ignore the towed vehicle's driver throttle/brakes/handbrake (nobody drives two cars at once).");
            _allowSteer = Config.Bind("Fixes", "AllowTowedSteering", false, "Let the towed vehicle's driver still steer.");
            _autoRelease = Config.Bind("Joint", "AutoReleaseGapMetres", 4f, "Release the tow automatically if the hitch stretches this far (crash or physics glitch). 0 = never.");
            Logger.LogInfo("Nomad Drive Tow 0.5 (preview) loaded. Host only. Press " + _key.Value + " near two vehicles.");
        }

        private void Update()
        {
            if (_vehicleManager == null) _vehicleManager = FindType("NomadDrive.Features.Vehicle.VehicleManager");
            if (_networkServer == null) _networkServer = FindType("Mirror.NetworkServer");
            if (_networkedNwh == null) _networkedNwh = FindType("NomadDrive.Features.Vehicle.Networking.NetworkedNWHVehicle");
            if (!_patched && _networkedNwh != null) { _patched = true; PatchGame(); }

            if (_joint != null && (_carrierRb == null || _towedRb == null)) Release("a vehicle was removed");

            if (KeyPressed(_key.Value))
            {
                if (_joint != null) { Release("released"); return; }
                TryAttach();
            }

            if (_joint != null && Time.time >= _nextDiag)
            {
                float dt = 3f;
                _nextDiag = Time.time + dt;
                var gap = Vector3.Distance(_towedRb.transform.TransformPoint(_joint.anchor),
                                           _carrierRb.transform.TransformPoint(_joint.connectedAnchor));
                var cp = _carrierRb.position; var tp = _towedRb.position;
                var cSpeed = (cp - _lastCarrierPos).magnitude / dt;
                var tSpeed = (tp - _lastTowedPos).magnitude / dt;
                _lastCarrierPos = cp; _lastTowedPos = tp;
                if (_autoRelease.Value > 0f && gap > _autoRelease.Value) { Toast("Tow released: the tow stretched " + gap.ToString("0.0") + " m"); Release("stretched"); return; }
                Logger.LogInfo("Tow: gap=" + gap.ToString("0.00") + "m  moved: carrier " + cSpeed.ToString("0.00") +
                               " m/s, towed " + tSpeed.ToString("0.00") + " m/s  jointForce=" +
                               _joint.currentForce.magnitude.ToString("0") + "N  towed: kinematic=" + _towedRb.isKinematic +
                               " sleeping=" + _towedRb.IsSleeping() + " handbrakeOn=" + HandbrakeOn(_towedVm) +
                               " wheelSimAsleep=" + WheelSimAsleep(_towedVm));
            }
        }

        // ---- patches: stop the game from holding the towed vehicle ---------------------------

        private void PatchGame()
        {
            try
            {
                var h = new Harmony("com.nunod87.nomaddrive.tow");
                foreach (var name in new[] { "ServerHoldDriverlessVehicle", "DampDriverlessMotion" })
                {
                    var m = AccessTools.Method(_networkedNwh, name);
                    if (m == null) { Logger.LogWarning("Tow: " + name + " not found."); continue; }
                    h.Patch(m, prefix: new HarmonyMethod(typeof(Plugin), nameof(SkipHoldPrefix)));
                    Logger.LogInfo("Tow: hooked " + name);
                }
                var gate = AccessTools.Method(_networkedNwh, "ApplyWheelSimulationGate");
                if (gate != null) { h.Patch(gate, prefix: new HarmonyMethod(typeof(Plugin), nameof(GatePrefix))); Logger.LogInfo("Tow: hooked ApplyWheelSimulationGate"); }
                else Logger.LogWarning("Tow: ApplyWheelSimulationGate not found.");

                var hbType = FindType("NomadDrive.Features.Vehicle.Modules.VehicleHandbrakeModule");
                var hold = hbType == null ? null : AccessTools.PropertyGetter(hbType, "IsParkingBrakeHolding");
                if (hold != null) { h.Patch(hold, prefix: new HarmonyMethod(typeof(Plugin), nameof(ParkingHoldPrefix))); Logger.LogInfo("Tow: hooked IsParkingBrakeHolding"); }
                else Logger.LogWarning("Tow: IsParkingBrakeHolding not found.");

                var vcType = FindType("NWH.VehiclePhysics2.VehicleController");
                var vcFixed = vcType == null ? null : AccessTools.Method(vcType, "FixedUpdate");
                if (vcFixed != null) { h.Patch(vcFixed, prefix: new HarmonyMethod(typeof(Plugin), nameof(VcFixedUpdatePrefix))); Logger.LogInfo("Tow: hooked VehicleController.FixedUpdate"); }
                else Logger.LogWarning("Tow: VehicleController.FixedUpdate not found.");
            }
            catch (Exception e) { Logger.LogError("Tow: patching failed: " + e); }
        }

        private static bool IsTowed(object instance)
        {
            var p = I;
            if (p == null || p._joint == null || p._towedVm == null) return false;
            var c = instance as Component;
            return c != null && c.transform.root == p._towedVm.transform.root;
        }

        private static bool SkipHoldPrefix(object __instance)
        {
            return !(I._skipDriverlessHold.Value && IsTowed(__instance)); // false = skip the game's method
        }

        private static bool GatePrefix(object __instance)
        {
            if (!(I._forceWheels.Value && IsTowed(__instance))) return true;
            SetWheels(__instance as Component, true);
            return false;
        }

        // The game says "parking brake holds" when the handbrake value is above 0.5; not while towing.
        private static bool ParkingHoldPrefix(object __instance, ref bool __result)
        {
            if (!(I._skipDriverlessHold.Value && IsTowed(__instance))) return true;
            __result = false;
            return false;
        }

        private static void SetWheels(Component nwh, bool on)
        {
            try
            {
                if (nwh == null) return;
                var wm = nwh.GetType().GetField("_wheelsManager", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(nwh);
                wm?.GetType().GetMethod("SetWheelSimulation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(wm, new object[] { on });
            }
            catch (Exception e) { I.Logger.LogWarning("Tow: SetWheelSimulation failed: " + e.Message); }
        }

        private static string HandbrakeOn(Component vm)
        {
            try
            {
                var m = vm.GetComponentsInChildren<Component>(true).FirstOrDefault(c => c != null && c.GetType().Name == "VehicleHandbrakeModule");
                if (m == null) return "no-module";
                var t = m.GetType();
                const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var on = t.GetProperty("IsHandbrakeOn", F)?.GetValue(m, null);
                var hold = t.GetProperty("IsParkingBrakeHolding", F)?.GetValue(m, null);
                var val = t.GetField("_handbrakeValue", F)?.GetValue(m);
                return "on=" + on + " parkingHold=" + hold + " value=" + val + " | " + BrakeInfo(vm);
            }
            catch (Exception e) { return "err:" + e.Message; }
        }

        // ---- NWH helpers (reflection) --------------------------------------------------------

        private const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static object Member(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            var f = t.GetField(name, BF);
            if (f != null) return f.GetValue(o);
            var p = t.GetProperty(name, BF);
            return p != null ? p.GetValue(o, null) : null;
        }

        private static bool SetMember(object o, string name, object value)
        {
            if (o == null) return false;
            var t = o.GetType();
            var f = t.GetField(name, BF);
            if (f != null) { f.SetValue(o, Convert.ChangeType(value, f.FieldType)); return true; }
            var p = t.GetProperty(name, BF);
            if (p != null && p.CanWrite) { p.SetValue(o, Convert.ChangeType(value, p.PropertyType), null); return true; }
            return false;
        }

        private static object GetVc(Component vm)
        {
            return Member(vm, "VehicleController")
                   ?? vm.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name == "VehicleController");
        }

        private static string BrakeInfo(Component vm)
        {
            try
            {
                var vc = GetVc(vm);
                var br = Member(vc, "brakes");
                var tr = Member(Member(vc, "powertrain"), "transmission");
                var en = Member(Member(vc, "powertrain"), "engine");
                return "brakeWhileIdle=" + Member(br, "brakeWhileIdle") + " brakeWhileDisabled=" + Member(br, "brakeWhileDisabled") +
                       " isBraking=" + Member(br, "IsBraking") + " gear=" + Member(tr, "Gear") + " engineRunning=" + Member(en, "IsRunning");
            }
            catch (Exception e) { return "brakeinfo err:" + e.Message; }
        }

        // Towed vehicle: no idle braking, neutral gear. Original values are restored on release.
        private bool _savedIdle, _savedDisabled; private bool _savedHave;

        private string PrepareTowed(Component vm)
        {
            try
            {
                var vc = GetVc(vm);
                if (vc == null) return "no VehicleController";
                var br = Member(vc, "brakes");
                var tr = Member(Member(vc, "powertrain"), "transmission");
                if (br != null)
                {
                    _savedIdle = (bool)Member(br, "brakeWhileIdle"); _savedDisabled = (bool)Member(br, "brakeWhileDisabled"); _savedHave = true;
                    SetMember(br, "brakeWhileIdle", false); SetMember(br, "brakeWhileDisabled", false);
                }
                if (tr != null) SetMember(tr, "Gear", 0);
                return BrakeInfo(vm);
            }
            catch (Exception e) { return "prepare failed: " + e.Message; }
        }

        private void RestoreTowed(Component vm)
        {
            try
            {
                if (vm == null || !_savedHave) return;
                var br = Member(GetVc(vm), "brakes");
                if (br != null) { SetMember(br, "brakeWhileIdle", _savedIdle); SetMember(br, "brakeWhileDisabled", _savedDisabled); }
                _savedHave = false;
            }
            catch { }
        }

        // While towed, nobody drives the towed vehicle: its driver's throttle/brakes/handbrake/clutch are zeroed
        // before the physics step (steering too, unless AllowTowedSteering). The towed car just rolls.
        private static void VcFixedUpdatePrefix(object __instance)
        {
            var p = I;
            if (p == null || p._joint == null || p._towedVm == null || !p._blockDriver.Value) return;
            var vcComp = __instance as Component;
            if (vcComp == null || vcComp.transform.root != p._towedVm.transform.root) return;
            try
            {
                var input = Member(__instance, "input");
                if (input == null) return;
                SetMember(input, "Throttle", 0f); SetMember(input, "Brakes", 0f);
                SetMember(input, "Handbrake", 0f); SetMember(input, "Clutch", 1f);
                if (!p._allowSteer.Value) SetMember(input, "Steering", 0f);
            }
            catch { }
        }

        // Mirror authority: a vehicle last driven by a client stays client-owned, so the host copy is
        // kinematic and driven by that client's snapshots. Give the towed vehicle to the host while towing,
        // and hand it back to the previous owner when the tow ends.
        private object _origOwner; private object _ownedIdentity; private bool _ownerChanged;

        private string TakeAuthority(Component vm)
        {
            try
            {
                _ownerChanged = false; _origOwner = null; _ownedIdentity = null;
                var id = vm.GetComponentsInChildren<Component>(true).FirstOrDefault(c => c != null && c.GetType().Name == "NetworkIdentity");
                if (id == null) return "no NetworkIdentity";
                const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var t = id.GetType();
                var owner = t.GetProperty("connectionToClient", F)?.GetValue(id, null);
                var owned = t.GetProperty("isOwned", F)?.GetValue(id, null);
                var local = _networkServer?.GetProperty("localConnection", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
                string res = "owner=" + (owner == null ? "none" : "client") + " isOwned=" + owned;
                if (local != null && !ReferenceEquals(owner, local))
                {
                    if (owner != null) t.GetMethod("RemoveClientAuthority", F)?.Invoke(id, null);
                    var ok = t.GetMethod("AssignClientAuthority", F)?.Invoke(id, new[] { local });
                    _origOwner = owner; _ownedIdentity = id; _ownerChanged = true;
                    res += " -> assigned to host: " + ok;
                }
                else res += " (already host-owned)";
                return res;
            }
            catch (Exception e) { return "authority failed: " + e.Message; }
        }

        private string GiveAuthorityBack()
        {
            try
            {
                if (!_ownerChanged || _ownedIdentity == null) return "ownership unchanged";
                _ownerChanged = false;
                const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var t = _ownedIdentity.GetType();
                if (_origOwner == null) return "no previous owner";
                // Only hand back if that client is still connected.
                var connId = _origOwner.GetType().GetProperty("connectionId", F)?.GetValue(_origOwner, null);
                var conns = _networkServer?.GetProperty("connections", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null) as System.Collections.IDictionary;
                if (conns == null || connId == null || !conns.Contains(connId)) return "previous owner gone, stays host-owned";
                t.GetMethod("RemoveClientAuthority", F)?.Invoke(_ownedIdentity, null);
                var ok = t.GetMethod("AssignClientAuthority", F)?.Invoke(_ownedIdentity, new[] { _origOwner });
                return "returned to previous owner: " + ok;
            }
            catch (Exception e) { return "return failed: " + e.Message; }
        }

        private static string WheelSimAsleep(Component vm)
        {
            try
            {
                var wm = vm.GetComponentsInChildren<Component>(true).FirstOrDefault(c => c != null && c.GetType().Name == "WheelsManager");
                var f = wm?.GetType().GetField("_wheelSimulationAsleep", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                return f != null ? f.GetValue(wm).ToString() : "?";
            }
            catch { return "?"; }
        }

        // ---- attach / release ----------------------------------------------------------------

        private void TryAttach()
        {
            if (!IsServer()) { Toast("Tow: only the host can do this."); return; }
            if (_vehicleManager == null) { Toast("Tow: vehicle type not found."); return; }

            var vms = FindObjectsOfType(_vehicleManager).Cast<Component>().ToList();
            var vehicles = vms.Select(v => new V(v)).Where(v => v.Rb != null).ToList();
            if (vehicles.Count < 2) { Toast("Tow: need two vehicles in the world."); return; }

            V bestC = null, bestT = null; float best = float.MaxValue;
            string why = null;
            foreach (var c in vehicles)
                foreach (var t in vehicles)
                {
                    if (c == t) continue;
                    if (_requireHeavier.Value && c.Rb.mass + 0.01f < t.Rb.mass) { why = "carrier must be heavier"; continue; }
                    if (_requireSameDirection.Value && Vector3.Dot(c.Root.forward, t.Root.forward) < 0.3f) { why = "vehicles must face the same way"; continue; }
                    var d = Vector3.Distance(c.RearPoint, t.KnobPoint);
                    if (d < best) { best = d; bestC = c; bestT = t; }
                }
            if (bestC == null) { Toast("Tow: no valid pair (" + (why ?? "?") + ")."); return; }
            if (best > _maxDistance.Value) { Toast("Tow: too far (" + best.ToString("0.0") + " m, max " + _maxDistance.Value + ")."); return; }

            var hitch = bestT.KnobPoint;
            _towedVm = bestT.Vm;
            var auth = _takeAuthority.Value ? TakeAuthority(bestT.Vm) : "skipped";
            Logger.LogInfo("Tow: towed authority: " + auth);
            if (_prepareTowed.Value) Logger.LogInfo("Tow: towed prepared: " + PrepareTowed(bestT.Vm));
            _joint = bestT.Root.gameObject.AddComponent<ConfigurableJoint>();
            _joint.connectedBody = bestC.Rb;
            _joint.autoConfigureConnectedAnchor = false;
            _joint.anchor = bestT.Root.InverseTransformPoint(hitch);
            _joint.connectedAnchor = bestC.Root.InverseTransformPoint(hitch);
            _joint.xMotion = _joint.yMotion = _joint.zMotion = ConfigurableJointMotion.Locked;
            _joint.angularXMotion = ConfigurableJointMotion.Limited;
            _joint.angularYMotion = ConfigurableJointMotion.Limited;
            _joint.angularZMotion = ConfigurableJointMotion.Limited;
            _joint.lowAngularXLimit = new SoftJointLimit { limit = -_pitchRollLimit.Value };
            _joint.highAngularXLimit = new SoftJointLimit { limit = _pitchRollLimit.Value };
            _joint.angularYLimit = new SoftJointLimit { limit = _yawLimit.Value };
            _joint.angularZLimit = new SoftJointLimit { limit = _pitchRollLimit.Value };
            _joint.enableCollision = false;
            _joint.projectionMode = JointProjectionMode.None;
            if (_breakForce.Value > 0f) { _joint.breakForce = _breakForce.Value; _joint.breakTorque = _breakForce.Value; }

            _carrierRb = bestC.Rb; _towedRb = bestT.Rb;
            _carrierName = bestC.Root.name; _towedName = bestT.Root.name;
            _carrierSleep = _carrierRb.sleepThreshold; _towedSleep = _towedRb.sleepThreshold;
            _carrierRb.sleepThreshold = 0f; _towedRb.sleepThreshold = 0f;
            _carrierRb.WakeUp(); _towedRb.WakeUp();
            _lastCarrierPos = _carrierRb.position; _lastTowedPos = _towedRb.position;
            _nextDiag = Time.time + 3f;

            if (_forceWheels.Value)
            {
                var nwh = bestT.Root.GetComponentsInChildren<Component>(true).FirstOrDefault(c => c != null && c.GetType().Name == "NetworkedNWHVehicle");
                SetWheels(nwh, true);
            }

            var msg = "Tow ON: " + _carrierName + " (" + _carrierRb.mass + " kg) tows " + _towedName + " (" + _towedRb.mass + " kg), gap " + best.ToString("0.0") + " m";
            Logger.LogInfo(msg + (bestC.UsedFallbackRear ? " [rear: fallback]" : " [rear: motorcycle holder]") +
                           (bestT.UsedFallbackKnob ? " [knob: fallback]" : " [knob: TowKnobSlot]") +
                           " towed handbrakeOn=" + HandbrakeOn(_towedVm) + " wheelSimAsleep=" + WheelSimAsleep(_towedVm));
            Toast(msg);
        }

        private void Release(string why)
        {
            if (_joint != null) Destroy(_joint);
            _joint = null;
            RestoreTowed(_towedVm);
            Logger.LogInfo("Tow: " + GiveAuthorityBack());
            if (_carrierRb != null) _carrierRb.sleepThreshold = _carrierSleep;
            if (_towedRb != null) _towedRb.sleepThreshold = _towedSleep;
            Logger.LogInfo("Tow OFF (" + why + "): " + _carrierName + " / " + _towedName);
            Toast("Tow OFF");
            _carrierRb = _towedRb = null; _towedVm = null;
        }

        // ---- vehicle wrapper -----------------------------------------------------------------

        private class V
        {
            public readonly Component Vm;
            public readonly Transform Root;
            public readonly Rigidbody Rb;
            public readonly bool UsedFallbackRear, UsedFallbackKnob;
            private readonly Transform _rear, _knob;

            public V(Component vm)
            {
                Vm = vm;
                Root = vm.transform;
                Rb = vm.GetComponent<Rigidbody>() ?? vm.GetComponentInParent<Rigidbody>();
                _rear = FindChild(Root, "UpgradeZone_MotorcycleHolder");
                _knob = FindChild(Root, "TowKnobSlot");
                UsedFallbackRear = _rear == null;
                UsedFallbackKnob = _knob == null;
            }

            public Vector3 RearPoint { get { return _rear != null ? _rear.position : Root.TransformPoint(new Vector3(0f, 0.8f, -2.6f)); } }
            public Vector3 KnobPoint { get { return _knob != null ? _knob.position : Root.TransformPoint(new Vector3(0f, 0.48f, 3.0f)); } }

            private static Transform FindChild(Transform root, string name)
            {
                return root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name);
            }
        }

        // ---- helpers -------------------------------------------------------------------------

        private bool IsServer()
        {
            try
            {
                var p = _networkServer?.GetProperty("active", BindingFlags.Public | BindingFlags.Static);
                return p != null && (bool)p.GetValue(null, null);
            }
            catch { return false; }
        }

        private static Type FindType(string full)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = a.GetType(full); if (t != null) return t; } catch { }
            }
            return null;
        }

        private static bool KeyPressed(string name)
        {
            try
            {
                var kb = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
                var cur = kb?.GetProperty("current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
                if (cur != null)
                {
                    var prop = kb.GetProperty(name.ToLowerInvariant() + "Key") ?? kb.GetProperty(name + "Key");
                    var ctrl = prop?.GetValue(cur, null);
                    var was = ctrl?.GetType().GetProperty("wasPressedThisFrame")?.GetValue(ctrl, null);
                    if (was is bool b) return b;
                }
            }
            catch { }
            try { return Input.GetKeyDown((KeyCode)Enum.Parse(typeof(KeyCode), name)); }
            catch { return false; }
        }

        private void Toast(string s) { _toast = s; _toastUntil = Time.time + 4f; Logger.LogInfo(s); }

        private void OnGUI()
        {
            if (_toast == null || Time.time > _toastUntil) return;
            var style = new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.UpperCenter };
            var r = new Rect(0, 60, Screen.width, 40);
            GUI.color = Color.black; GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), _toast, style);
            GUI.color = Color.white; GUI.Label(r, _toast, style);
        }
    }
}
