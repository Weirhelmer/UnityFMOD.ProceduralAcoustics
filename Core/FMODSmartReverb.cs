using UnityEngine;
using FMODUnity;

/// <summary>
/// Procedural Reverb Controller using Fibonacci Sphere Sampling.
/// Scans the environment volumetrically to drive Room Size and Reverb Amount parameters in real-time.
/// </summary>
public class FMODSmartReverb : MonoBehaviour
{
    [Header("Configuration")]
    public bool isGlobal = false;
    public LayerMask environmentLayer;
    public float updateRate = 0.2f;
    public float smoothSpeed = 3.0f;

    [Header("FMOD Parameters")]
    public string reverbParam = "ReverbAmount";
    public string roomSizeParam = "RoomSize";
    [Tooltip("Optional: Target specific emitter instead of Global FMOD System")]
    public StudioEventEmitter targetEmitter;

    [Header("Scanning Settings")]
    public float maxScanDistance = 100.0f;
    [Range(6, 100)]
    public int raysCount = 30;

    [Header("Acoustic Calibration")]
    [Tooltip("Maps Enclosure Factor (0-1) to Reverb Amount")]
    public AnimationCurve reverbAmountCurve;
    [Tooltip("Maps Average Distance to Room Size (filtered by enclosure)")]
    public AnimationCurve roomSizeCurve;

    // Public Properties
    public float CurrentReverb { get; private set; }
    public float CurrentRoomSize { get; private set; }

    /// <summary>
    /// Calculated ratio of rays hitting obstacles. 0.0 = Open Field, 1.0 = Fully Enclosed.
    /// Useful for driving Ambience mixers.
    /// </summary>
    public float EnclosureFactor { get; private set; }

    // Internal
    private float targetReverb;
    private float targetRoomSize;
    private float timer;
    private Vector3[] rayDirections;

    // Debug Cache
    private Vector3[] debugHitPoints;
    private bool[] debugDidHit;

    void Start()
    {
        InitializeFibonacciSphere();

        if (!isGlobal && targetEmitter == null)
            targetEmitter = GetComponent<StudioEventEmitter>();

        if (roomSizeCurve == null || roomSizeCurve.length == 0) SetupDefaultCurves();
    }

    void Reset() { SetupDefaultCurves(); }

    /// <summary>
    /// Generates equidistributed points on a sphere using the Fibonacci Lattice algorithm.
    /// Ensures uniform volumetric sampling regardless of ray count.
    /// </summary>
    void InitializeFibonacciSphere()
    {
        rayDirections = new Vector3[raysCount];
        debugHitPoints = new Vector3[raysCount];
        debugDidHit = new bool[raysCount];

        float goldenRatio = (1 + Mathf.Sqrt(5)) / 2;
        float angleIncrement = Mathf.PI * 2 * goldenRatio;

        for (int i = 0; i < raysCount; i++)
        {
            float t = (float)i / raysCount;

            // Acos ensures equal area distribution (prevents clumping at poles)
            float inclination = Mathf.Acos(1 - 2 * t);
            // Golden Angle ensures spiral distribution
            float azimuth = angleIncrement * i;

            float x = Mathf.Sin(inclination) * Mathf.Cos(azimuth);
            float y = Mathf.Sin(inclination) * Mathf.Sin(azimuth);
            float z = Mathf.Cos(inclination);

            rayDirections[i] = new Vector3(x, y, z);
        }
    }

    void SetupDefaultCurves()
    {
        // Default calibration for generic environments
        roomSizeCurve = new AnimationCurve(new Keyframe(0, 0), new Keyframe(0.1f, 0.4f), new Keyframe(0.3f, 0.7f), new Keyframe(1, 1));
        reverbAmountCurve = new AnimationCurve(new Keyframe(0, 0), new Keyframe(0.2f, 0.0f), new Keyframe(1, 1));
    }

    void Update()
    {
        if (!isGlobal && targetEmitter != null && !targetEmitter.IsPlaying()) return;

        // Throttled update loop
        timer += Time.deltaTime;
        if (timer >= updateRate)
        {
            PerformScan();
            timer = 0f;
        }

        // Interpolate parameters for smooth audio transitions
        CurrentReverb = Mathf.Lerp(CurrentReverb, targetReverb, Time.deltaTime * smoothSpeed);
        CurrentRoomSize = Mathf.Lerp(CurrentRoomSize, targetRoomSize, Time.deltaTime * smoothSpeed);

        ApplyFMODParameters();
    }

    void ApplyFMODParameters()
    {
        if (isGlobal && RuntimeManager.HasBankLoaded("Master"))
        {
            RuntimeManager.StudioSystem.setParameterByName(reverbParam, CurrentReverb);
            RuntimeManager.StudioSystem.setParameterByName(roomSizeParam, CurrentRoomSize);
        }
        else if (targetEmitter != null && targetEmitter.EventInstance.isValid())
        {
            targetEmitter.SetParameter(reverbParam, CurrentReverb);
            targetEmitter.SetParameter(roomSizeParam, CurrentRoomSize);
        }
    }

    void PerformScan()
    {
        if (rayDirections == null || rayDirections.Length != raysCount) InitializeFibonacciSphere();

        int hits = 0;
        float totalDist = 0f;

        for (int i = 0; i < raysCount; i++)
        {
            Vector3 dir = transform.TransformDirection(rayDirections[i]);

            if (Physics.Raycast(transform.position, dir, out RaycastHit hit, maxScanDistance, environmentLayer))
            {
                hits++;
                totalDist += hit.distance;

                // Cache for visualization
                debugDidHit[i] = true;
                debugHitPoints[i] = hit.point;
            }
            else
            {
                debugDidHit[i] = false;
                debugHitPoints[i] = transform.position + (dir * maxScanDistance);
            }
        }

        // Calculate acoustic metrics
        float enclosureRatio = (float)hits / raysCount;
        EnclosureFactor = enclosureRatio;

        // 1. Reverb Amount: Directly proportional to enclosure (reflections)
        targetReverb = reverbAmountCurve.Evaluate(enclosureRatio);

        // 2. Room Size: Based on average distance, but gated by enclosure.
        // Prevents large "Room Size" values in open fields (where rays hit the ground).
        float avgDist = (hits > 0) ? (totalDist / hits) : 0f;
        float normalizedDist = Mathf.Clamp01(avgDist / maxScanDistance);

        // Smart Gating: Only apply RoomSize if enclosure is significant (> 50%)
        float roomFactor = Mathf.InverseLerp(0.5f, 0.8f, enclosureRatio);
        targetRoomSize = roomSizeCurve.Evaluate(normalizedDist) * roomFactor;
    }

    void OnDrawGizmos()
    {
        if (Application.isPlaying && debugHitPoints != null && debugHitPoints.Length > 0)
        {
            // Runtime Visualization
            for (int i = 0; i < debugHitPoints.Length; i++)
            {
                Gizmos.color = debugDidHit[i] ? new Color(0f, 1f, 1f, 0.8f) : new Color(1f, 1f, 1f, 0.1f);
                Gizmos.DrawLine(transform.position, debugHitPoints[i]);
                if (debugDidHit[i]) Gizmos.DrawSphere(debugHitPoints[i], 0.1f);
            }
        }
        else
        {
            // Editor Preview (Yellow Sphere)
            Gizmos.color = new Color(1f, 0.92f, 0.016f, 0.3f);
            if (rayDirections == null || rayDirections.Length != raysCount) InitializeFibonacciSphere();

            foreach (var localDir in rayDirections)
            {
                Vector3 worldDir = transform.TransformDirection(localDir);
                Gizmos.DrawRay(transform.position, worldDir * maxScanDistance);
            }
            Gizmos.color = new Color(1f, 1f, 1f, 0.1f);
            Gizmos.DrawWireSphere(transform.position, maxScanDistance);
        }
    }
}