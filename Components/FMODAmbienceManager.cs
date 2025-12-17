using UnityEngine;
using FMODUnity;

/// <summary>
/// Manages environmental ambience blending based on volumetric enclosure data.
/// Interpolates between "Outdoor" and "Indoor" states using data from the Fibonacci Scanner.
/// </summary>
public class FMODAmbienceManager : MonoBehaviour
{
    [Header("Dependencies")]
    [Tooltip("Reference to the active volumetric scanner (usually on the Player/Listener).")]
    public FMODSmartReverb scannerSource;

    [Tooltip("Target emitter playing the 2D ambience event.")]
    public StudioEventEmitter ambienceEmitter;

    [Header("FMOD Configuration")]
    [Tooltip("FMOD Parameter Name (0.0 = Outdoors, 1.0 = Indoors).")]
    public string mixParameterName = "IndoorsMix";

    [Header("Response Settings")]
    [Tooltip("Smoothing time (in seconds) for blending between environments.")]
    public float smoothTime = 2.0f;

    [Header("Thresholds")]
    [Tooltip("Enclosure factor below which the environment is considered 'Outdoors' (ignoring ground hits).")]
    [Range(0f, 1f)] public float outdoorThreshold = 0.5f;

    [Tooltip("Enclosure factor above which the environment is considered fully 'Indoors'.")]
    [Range(0f, 1f)] public float indoorThreshold = 0.9f;

    // Public read-only property for debugging
    public float CurrentMix { get; private set; }

    // Internal smoothing state
    private float velocity = 0f;

    void Start()
    {
        // Auto-resolve dependency if missing
        if (scannerSource == null)
            scannerSource = FindObjectOfType<FMODSmartReverb>();
    }

    void Update()
    {
        if (scannerSource == null || ambienceEmitter == null) return;

        // 1. Fetch raw volumetric data (0.0 = Open Field, 1.0 = Tight Space)
        float enclosureFactor = scannerSource.EnclosureFactor;

        // 2. Remap Logic:
        // We filter out the "floor hits" (approx 0.5 in open fields) so the mix remains 0.0 until walls appear.
        // Transitions linearly to 1.0 as the space becomes fully enclosed.
        float targetMix = Mathf.InverseLerp(outdoorThreshold, indoorThreshold, enclosureFactor);

        // 3. Apply smoothing to prevent jarring ambience cuts
        CurrentMix = Mathf.SmoothDamp(CurrentMix, targetMix, ref velocity, smoothTime);

        // 4. Drive FMOD Parameter
        if (ambienceEmitter.IsPlaying())
        {
            ambienceEmitter.SetParameter(mixParameterName, CurrentMix);
        }
    }
}