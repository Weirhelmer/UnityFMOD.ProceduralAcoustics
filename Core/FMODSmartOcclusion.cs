using UnityEngine;
using FMODUnity;

/// <summary>
/// Handles volumetric occlusion and diffraction for FMOD Audio Sources.
/// Uses a multi-ray approach to simulate sound wrapping around corners and passing through obstacles.
/// </summary>
public class FMODSmartOcclusion : MonoBehaviour
{
    [Header("FMOD Integration")]
    public StudioEventEmitter emitter;
    [Tooltip("Target FMOD Parameter (0.0 = Clear, 1.0 = Occluded)")]
    public string occlusionParameter = "Occlusion";

    [Header("Scanner Settings")]
    public float roomScanRadius = 5.0f;
    public LayerMask obstacleLayer;

    [Header("Volumetric Sampling")]
    [Tooltip("Spread angle for the raycast cone.")]
    public float diffractionSpread = 0.8f;
    [Range(1, 10)]
    public int raysCount = 6;

    [Header("Corner & Diffraction Logic")]
    [Tooltip("Ignore occlusion if the obstacle is within this distance (prevents false positives near walls).")]
    public float nearFieldThreshold = 1.0f;

    [Tooltip("Simulates transmission loss over distance when enclosed.")]
    public float transmissionMaxDist = 25.0f;
    public AnimationCurve transmissionCurve = new AnimationCurve(new Keyframe(0, 0), new Keyframe(1, 0.8f));

    [Tooltip("Distance at which sound fully diffracts around an obstacle.")]
    public float diffractionDist = 10.0f;
    public float smoothSpeed = 3.0f;

    // Internal state
    private float currentOcclusion = 0.0f;
    private bool isEnclosed = false;
    private Transform listener;
    private Vector3[] offsets;
    private float scanTimer = 0f;

    // Pre-allocated directions for environment check
    private readonly Vector3[] scanDirs = { Vector3.up, Vector3.down, Vector3.forward, Vector3.back, Vector3.left, Vector3.right };

    void Start()
    {
        if (emitter == null) emitter = GetComponent<StudioEventEmitter>();

        // Listener fallback strategy
        var listenerObj = FindObjectOfType<StudioListener>();
        if (listenerObj != null) listener = listenerObj.transform;
        else if (Camera.main != null) listener = Camera.main.transform;

        GenerateRayOffsets();
        CheckEnclosure();
    }

    /// <summary>
    /// Pre-calculates ray offsets for the volumetric cone to avoid runtime trigonometry.
    /// </summary>
    void GenerateRayOffsets()
    {
        offsets = new Vector3[raysCount];
        offsets[0] = Vector3.zero; // Center ray

        for (int i = 1; i < raysCount; i++)
        {
            // Distribute rays in a circle around the center
            float angle = (i * 360f) / (raysCount - 1);
            offsets[i] = Quaternion.Euler(0, 0, angle) * Vector3.up * diffractionSpread;
        }
    }

    /// <summary>
    /// Low-frequency check to determine if the source is physically inside a room.
    /// </summary>
    void CheckEnclosure()
    {
        int hitCount = 0;
        foreach (var dir in scanDirs)
        {
            if (Physics.Raycast(transform.position, dir, roomScanRadius, obstacleLayer))
                hitCount++;
        }
        // Consider enclosed if blocked on majority of sides (4+ out of 6)
        isEnclosed = (hitCount >= 4);
    }

    void Update()
    {
        if (listener == null || emitter == null) return;

        // Optimization: Distance culling based on FMOD settings
        float distance = Vector3.Distance(listener.position, transform.position);
        if (emitter.OverrideAttenuation && distance > emitter.OverrideMaxDistance) return;

        // Throttled environment scan (2Hz)
        scanTimer += Time.deltaTime;
        if (scanTimer > 0.5f) { CheckEnclosure(); scanTimer = 0f; }

        CalculateOcclusion(distance);
    }

    void CalculateOcclusion(float distance)
    {
        Vector3 toPlayer = listener.position - transform.position;
        bool centerIsBlocked = Physics.Raycast(transform.position, toPlayer, distance, obstacleLayer);

        float blockedCount = 0;
        Quaternion lookRot = Quaternion.LookRotation(toPlayer);

        // Volumetric Raycasting Loop
        for (int i = 0; i < offsets.Length; i++)
        {
            Vector3 worldOffset = lookRot * offsets[i];
            Vector3 targetPos = listener.position + worldOffset;
            Vector3 dir = targetPos - transform.position;
            float targetDist = dir.magnitude;

            if (Physics.Raycast(transform.position, dir, out RaycastHit hit, targetDist, obstacleLayer))
            {
                // Smart Corner Logic: Ignore hits that are too close to the target (false positives)
                // This prevents occlusion when the player is simply standing near a wall
                float distFromHitToTarget = targetDist - hit.distance;
                bool isNearField = (distFromHitToTarget < nearFieldThreshold) && (!centerIsBlocked);

                if (isNearField)
                {
#if UNITY_EDITOR
                    Debug.DrawLine(transform.position, hit.point, Color.yellow); // Ignored ray
#endif
                }
                else
                {
                    blockedCount++;
#if UNITY_EDITOR
                    Debug.DrawLine(transform.position, hit.point, new Color(1, 0, 0, 0.5f)); // Blocked ray
#endif
                }
            }
            else
            {
#if UNITY_EDITOR
                Debug.DrawLine(transform.position, targetPos, new Color(0, 1, 0, 0.1f)); // Clear ray
#endif
            }
        }

        float physicalOcclusion = blockedCount / raysCount;
        float targetOcclusion = physicalOcclusion;

        // Transmission vs Diffraction logic
        if (isEnclosed)
        {
            // Simulate sound dampening through walls/doors based on distance
            float t = Mathf.Clamp01(distance / transmissionMaxDist);
            float transmissionFactor = transmissionCurve.Evaluate(t);
            targetOcclusion = Mathf.Max(physicalOcclusion, transmissionFactor);
        }
        else if (physicalOcclusion > 0.9f) // Almost fully occluded outdoors
        {
            // Simulate diffraction (wrapping) around corners
            if (Physics.Raycast(transform.position, toPlayer, out RaycastHit hit, distance, obstacleLayer))
            {
                float distBehind = Vector3.Distance(hit.point, listener.position);
                float wrapFactor = Mathf.Clamp01(distBehind / diffractionDist);
                targetOcclusion = Mathf.Lerp(physicalOcclusion, 0.5f, wrapFactor);
            }
        }

        // Smooth parameter update to prevent popping
        currentOcclusion = Mathf.Lerp(currentOcclusion, targetOcclusion, Time.deltaTime * smoothSpeed);

        if (emitter.IsPlaying())
            emitter.SetParameter(occlusionParameter, currentOcclusion);
    }
}