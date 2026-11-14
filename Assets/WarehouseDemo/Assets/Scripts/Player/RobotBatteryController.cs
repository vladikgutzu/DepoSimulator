using System.Collections;
using StarterAssets;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// Local presentation and offline authority for a robot battery.
/// In a network session NetworkPlayer owns the authoritative values; this component only docks
/// the owning body and reflects those values in the HUD.
public sealed class RobotBatteryController : MonoBehaviour
{
    public static RobotBatteryController Local { get; private set; }
    static readonly int SpeedAnimId = Animator.StringToHash("Speed");
    static readonly int MotionSpeedAnimId = Animator.StringToHash("MotionSpeed");

    public static RobotBatteryController EnsureLocal()
    {
        if (Local != null) return Local;

        GameObject owner = null;
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening &&
            NetworkPlayer.Local != null)
            owner = NetworkPlayer.Local.gameObject;
        else
            owner = PlayerFinder.Find()?.gameObject;

        if (owner == null) return null;
        var battery = owner.GetComponent<RobotBatteryController>()
                   ?? owner.GetComponentInChildren<RobotBatteryController>(true)
                   ?? owner.AddComponent<RobotBatteryController>();
        Local = battery;
        return battery;
    }

    public const float OfflineDrainPerSecond = 100f / (40f * 60f);
    public const float SlowRatePerSecond = 1.25f;
    public const float FastRatePerSecond = 5f;
    public const int SlowPricePerPercent = 2;
    public const int FastPricePerPercent = 6;
    const float ChargingHeightOffset = 0.05f;
    const float ChargingExitVerticalSpeed = 0.55f;

    ThirdPersonController _controller;
    CharacterController _character;
    Animator _animator;
    NetworkPlayer _networkPlayer;
    PlayerInteraction _interaction;
    bool _interactionWasEnabled;
    Coroutine _dockRoutine;
    float _offlineCharge = 100f;
    float _offlineCapacity = 100f;
    bool _offlineCharging;
    bool _offlineFast;
    int _offlineStation = -1;
    float _offlineBillingRemainder;
    bool _wasCharging;
    bool _batteryGuidanceRequested;
    float _ignoreStopInputUntil;
    float _chargingPoseWeight;
    float _preDockBodyY;
    bool _hasPreDockBodyY;
    int _lastKnownStation = -1;
    Collider[] _ignoredStationColliders;
    Collider[] _ignoredPlayerColliders;

    public float ChargePercent => IsNetworked
        ? (_networkPlayer != null ? _networkPlayer.BatteryPercentNet.Value : 100f)
        : _offlineCharge;
    public float MaxChargePercent => IsNetworked
        ? (_networkPlayer != null ? _networkPlayer.BatteryCapacityNet.Value : 100f)
        : _offlineCapacity;
    public bool IsCharging => IsNetworked
        ? _networkPlayer != null && _networkPlayer.IsChargingNet.Value
        : _offlineCharging;
    public bool IsFastCharging => IsNetworked
        ? _networkPlayer != null && _networkPlayer.FastChargingNet.Value
        : _offlineFast;
    public int StationIndex => IsNetworked
        ? (_networkPlayer != null ? _networkPlayer.ChargingStationNet.Value : -1)
        : _offlineStation;
    bool IsNetworked => NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    void Awake()
    {
        _controller = GetComponent<ThirdPersonController>()
                   ?? GetComponentInChildren<ThirdPersonController>(true);
        _character = GetComponent<CharacterController>()
                  ?? GetComponentInChildren<CharacterController>(true);
        _animator = GetComponent<Animator>();
        if (_animator == null) _animator = GetComponentInChildren<Animator>(true);
        _networkPlayer = GetComponent<NetworkPlayer>() ?? GetComponentInParent<NetworkPlayer>();
        _interaction = GetComponentInChildren<PlayerInteraction>(true);
    }

    void OnEnable()
    {
        if (!IsNetworked || _networkPlayer == null || _networkPlayer.IsOwner)
            Local = this;
    }

    void OnDisable()
    {
        if (Local == this)
        {
            Local = null;
            AudioManager.Instance?.StopChargingHumImmediately();
        }
        RestoreControl();
    }

    void Update()
    {
        if (IsNetworked && (_networkPlayer == null || !_networkPlayer.IsOwner)) return;

        if (!IsNetworked) TickOffline();

        bool charging = IsCharging;
        if (charging) _lastKnownStation = StationIndex;
        if (charging && !_wasCharging)
        {
            _ignoreStopInputUntil = Time.unscaledTime + 0.35f;
            BeginDock(StationIndex);
        }
        else if (!charging && _wasCharging)
        {
            bool fullyCharged = ChargePercent >= MaxChargePercent - 0.001f;
            AudioManager.Instance?.StopChargingHum(fullyCharged);
            if (_dockRoutine != null) StopCoroutine(_dockRoutine);
            _dockRoutine = StartCoroutine(StepAwayAndRestore());
        }
        _wasCharging = charging;

        (ChargingUIController.Instance ?? ChargingUIController.Ensure())?.UpdateBattery(this);
        float batteryPercent = MaxChargePercent > 0f
            ? ChargePercent / MaxChargePercent * 100f
            : 100f;
        HUDController.Instance?.SetBatteryLowPowerLevel(batteryPercent);

        if (!_batteryGuidanceRequested && ChargePercent <= 80f && TutorialManager.Instance != null)
        {
            _batteryGuidanceRequested = true;
            TutorialManager.Instance.RequestBatteryIntro();
        }

        if (charging && Time.unscaledTime >= _ignoreStopInputUntil &&
            GameInputBindings.WasPressed(GameBindingAction.Interact))
            StopCharging();
    }

    void LateUpdate()
    {
        if (IsNetworked) return; // NetworkPlayer applies the same pose on every multiplayer copy.
        bool docked = IsCharging && _animator != null && _animator.GetFloat(SpeedAnimId) < 0.1f;
        float step = Time.unscaledDeltaTime / ChargingPoseUtility.BlendSeconds;
        _chargingPoseWeight = Mathf.MoveTowards(_chargingPoseWeight, docked ? 1f : 0f, step);
        ChargingPoseUtility.Apply(_animator, _chargingPoseWeight);
    }

    void TickOffline()
    {
        if (!_offlineCharging)
        {
            if (GameManager.Instance?.GameStarted == true)
                _offlineCharge = Mathf.Max(0f, _offlineCharge - OfflineDrainPerSecond * Time.deltaTime);
            return;
        }

        float wanted = Mathf.Min(_offlineCapacity - _offlineCharge,
            (_offlineFast ? FastRatePerSecond : SlowRatePerSecond) * Time.deltaTime);
        if (wanted <= 0f) { StopCharging(); return; }

        _offlineBillingRemainder += wanted * (_offlineFast ? FastPricePerPercent : SlowPricePerPercent);
        int bill = Mathf.FloorToInt(_offlineBillingRemainder);
        if (bill > 0)
        {
            if (GameManager.Instance == null || !GameManager.Instance.TrySpend(bill))
            {
                HUDController.Instance?.AddNotification("Charging stopped", "Team budget is too low.", HUDController.NotifType.Warning);
                StopCharging();
                return;
            }
            _offlineBillingRemainder -= bill;
        }
        _offlineCharge = Mathf.Min(_offlineCapacity, _offlineCharge + wanted);
        if (_offlineCharge >= _offlineCapacity - 0.001f) StopCharging();
    }

    public void RequestStart(ChargingStation station, bool fast)
    {
        if (station == null || IsCharging) return;
        if (ChargePercent >= MaxChargePercent - 0.1f)
        {
            HUDController.Instance?.AddNotification("Battery full", "This robot does not need charging.", HUDController.NotifType.Success);
            return;
        }

        // The shared multiplayer budget is authoritative on the server. A connected client's
        // local GameManager score can be stale (or remain zero), so it must not block this RPC.
        if (IsNetworked)
        {
            _networkPlayer?.RequestStartChargingServerRpc(station.StationIndex, fast);
            return;
        }

        int minimumChargeCost = fast ? FastPricePerPercent : SlowPricePerPercent;
        if (GameManager.Instance == null || GameManager.Instance.Score < minimumChargeCost)
        {
            HUDController.Instance?.AddNotification(
                "Charging unavailable",
                $"Not enough money. { (fast ? "Fast" : "Slow") } charge requires at least ${minimumChargeCost:N0}.",
                HUDController.NotifType.Warning);
            return;
        }

        _offlineStation = station.StationIndex;
        _offlineFast = fast;
        _offlineBillingRemainder = 0f;
        _offlineCharging = true;
    }

    public void StopCharging()
    {
        if (IsNetworked) _networkPlayer?.RequestStopChargingServerRpc();
        else
        {
            _offlineCharging = false;
            _offlineFast = false;
            _offlineStation = -1;
        }
    }

    public void SetChargeForTesting(float percent)
    {
        percent = Mathf.Clamp(percent, 0f, MaxChargePercent);
        if (IsNetworked) _networkPlayer?.SetBatteryForTestingServerRpc(percent);
        else _offlineCharge = percent;
        (ChargingUIController.Instance ?? ChargingUIController.Ensure())?.UpdateBattery(this);
    }

    public float IncreaseCapacity(float amount)
    {
        if (IsNetworked) return MaxChargePercent;
        _offlineCapacity = Mathf.Clamp(_offlineCapacity + Mathf.Max(0f, amount), 100f, 200f);
        (ChargingUIController.Instance ?? ChargingUIController.Ensure())?.UpdateBattery(this);
        return _offlineCapacity;
    }

    void BeginDock(int stationIndex)
    {
        (ChargingUIController.Instance ?? ChargingUIController.Ensure())?.ShowCharging();
        Transform body = ResolveMovementBody();
        _preDockBodyY = body.position.y;
        _hasPreDockBodyY = true;
        _interactionWasEnabled = _interaction != null && _interaction.enabled;
        if (_interactionWasEnabled) _interaction.enabled = false;
        if (_dockRoutine != null) StopCoroutine(_dockRoutine);
        _dockRoutine = StartCoroutine(DockRoutine(ChargingStation.ByIndex(stationIndex)));
    }

    IEnumerator DockRoutine(ChargingStation station)
    {
        if (station == null || station.ChargePoint == null) { StopCharging(); yield break; }
        IgnoreStationCollisions(station, true);
        if (_controller != null) { _controller.ResetLocomotionSpeed(); _controller.MovementLocked = true; }

        Transform target = station.ChargePoint;
        Transform body = ResolveMovementBody();
        Vector3 dockForward = target.position - station.transform.position;
        dockForward.y = 0f;
        if (dockForward.sqrMagnitude < 0.001f) dockForward = target.forward;
        dockForward.Normalize();
        float deadline = Time.unscaledTime + 8f;
        while (IsCharging && Time.unscaledTime < deadline)
        {
            Vector3 delta = target.position - body.position;
            delta.y = 0f;
            float distance = delta.magnitude;
            if (distance <= 0.06f) break;

            Vector3 step = delta.normalized * Mathf.Min(distance, 1.25f * Time.unscaledDeltaTime);
            if (_character != null && _character.enabled) _character.Move(step);
            else body.position += step;
            body.rotation = Quaternion.RotateTowards(body.rotation,
                Quaternion.LookRotation(delta.normalized, Vector3.up), 360f * Time.unscaledDeltaTime);
            SetAutomaticWalkAnimation(true);
            yield return null;
        }

        if (!IsCharging) yield break;
        // CharacterController.Move can stop a few centimetres short and its Transform is not
        // necessarily the NetworkPlayer Transform. Snap the same body that was actually moved to
        // the authored ChargePoint so every station produces an exact, repeatable dock pose.
        bool characterWasEnabled = _character != null && _character.enabled;
        if (characterWasEnabled) _character.enabled = false;
        // ChargePoint defines the exact horizontal dock position and facing. Lift the robot a
        // little above its approach height so the charging pose reads as standing on the platform.
        float chargingY = (_hasPreDockBodyY ? _preDockBodyY : body.position.y) + ChargingHeightOffset;
        Vector3 groundedDockPosition = new(target.position.x, chargingY, target.position.z);
        body.SetPositionAndRotation(groundedDockPosition,
            Quaternion.LookRotation(dockForward, Vector3.up));
        if (characterWasEnabled)
        {
            _character.enabled = true;
            // Toggling a CharacterController rebuilds its native collider. Re-apply the ignored
            // station pairs immediately, otherwise PhysX overlap recovery ejects the robot from
            // the station on the very frame it reaches ChargePoint.
            IgnoreStationCollisions(station, true);
        }
        SetAutomaticWalkAnimation(false);
        // The electrical loop belongs to active charging only: never play it during the
        // automatic walk into or out of the station.
        AudioManager.Instance?.StartChargingHum();
        _dockRoutine = null;
    }

    IEnumerator StepAwayAndRestore()
    {
        (ChargingUIController.Instance ?? ChargingUIController.Ensure())?.HideCharging();
        var station = ChargingStation.ByIndex(_lastKnownStation);
        Transform body = ResolveMovementBody();
        // Leave the bay in the same direction the robot faces while charging.
        Vector3 away = body.forward;
        away.y = 0f;
        if (away.sqrMagnitude < 0.001f) away = Vector3.forward;
        away.Normalize();
        // Start locomotion before the first exit frame so a manual E disconnect visibly blends
        // from the charging A-pose into walking instead of sliding away for one frame.
        SetAutomaticWalkAnimation(true);
        float started = Time.unscaledTime;
        float deadline = started + 2f;
        while (Time.unscaledTime < deadline)
        {
            float dt = Time.unscaledDeltaTime;
            Vector3 step = away * 1.3f * dt;
            if (_hasPreDockBodyY)
            {
                float nextY = Mathf.MoveTowards(body.position.y, _preDockBodyY,
                    ChargingExitVerticalSpeed * dt);
                step.y = nextY - body.position.y;
            }
            if (_character != null && _character.enabled) _character.Move(step);
            else body.position += step;
            // Always make a visible step, then keep walking only while the controller centre is
            // still inside one of the station volumes. Collision is restored outside the bay so
            // PhysX cannot eject the robot sideways on the restore frame.
            if (Time.unscaledTime - started >= 0.55f && !InsideIgnoredStation()) break;
            yield return null;
        }
        // The normal 0.55-second exit has enough time to cover the full 0.05-unit descent. Correct
        // any tiny CharacterController remainder before restoring station collisions.
        if (_hasPreDockBodyY && !Mathf.Approximately(body.position.y, _preDockBodyY))
        {
            bool characterWasEnabled = _character != null && _character.enabled;
            if (characterWasEnabled) _character.enabled = false;
            Vector3 restoredPosition = body.position;
            restoredPosition.y = _preDockBodyY;
            body.position = restoredPosition;
            if (characterWasEnabled) _character.enabled = true;
        }
        IgnoreStationCollisions(null, false);
        RestoreControl();
        _hasPreDockBodyY = false;
        _lastKnownStation = -1;
        _dockRoutine = null;
    }

    Transform ResolveMovementBody()
    {
        if (_character != null) return _character.transform;
        if (_controller != null) return _controller.transform;
        return transform;
    }

    void SetAutomaticWalkAnimation(bool moving)
    {
        if (_animator == null) return;
        // The locomotion blend tree's exact walk clip sits at Speed=2; run starts above it.
        float walkSpeed = moving ? 2f : 0f;
        _animator.SetFloat(SpeedAnimId, walkSpeed);
        _animator.SetFloat(MotionSpeedAnimId, moving ? 1f : 0f);
    }

    void IgnoreStationCollisions(ChargingStation station, bool ignore)
    {
        if (ignore)
        {
            _ignoredStationColliders = station != null
                ? station.GetComponentsInChildren<Collider>(true)
                : null;
            Transform playerRoot = _controller != null ? _controller.transform.root : transform.root;
            _ignoredPlayerColliders = playerRoot.GetComponentsInChildren<Collider>(true);
            SetStationCollisionIgnored(true);
            return;
        }

        SetStationCollisionIgnored(false);
        _ignoredStationColliders = null;
        _ignoredPlayerColliders = null;
    }

    void SetStationCollisionIgnored(bool ignored)
    {
        if (_ignoredStationColliders == null || _ignoredPlayerColliders == null) return;
        foreach (var playerCollider in _ignoredPlayerColliders)
        {
            if (playerCollider == null) continue;
            foreach (var stationCollider in _ignoredStationColliders)
            {
                if (stationCollider == null || stationCollider == playerCollider) continue;
                Physics.IgnoreCollision(playerCollider, stationCollider, ignored);
            }
        }
    }

    bool InsideIgnoredStation()
    {
        if (_character == null || _ignoredStationColliders == null) return false;
        Vector3 centre = _character.bounds.center;
        foreach (var collider in _ignoredStationColliders)
            if (collider != null && collider.enabled && collider.bounds.Contains(centre)) return true;
        return false;
    }

    void RestoreControl()
    {
        IgnoreStationCollisions(null, false);
        SetAutomaticWalkAnimation(false);
        if (_controller != null)
        {
            _controller.MovementLocked = false;
            _controller.ResetLocomotionSpeed();
        }
        if (_interaction != null && _interactionWasEnabled) _interaction.enabled = true;
        _interactionWasEnabled = false;
    }
}
