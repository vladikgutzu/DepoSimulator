using UnityEngine;

/// Player-facing control preferences shared by the character and drivable equipment.
/// Values are deliberately simple multipliers so authored per-controller sensitivities remain
/// useful while the player can still tune the overall feel from either menu.
public static class GameSettings
{
    const string SensitivityKey = "Controls.LookSensitivity";
    const string InvertYKey     = "Controls.InvertY";
    const string MasterVolumeKey = "Audio.MasterVolume";
    const string MusicEnabledKey = "Audio.MusicEnabled";
    const string MusicVolumeKey  = "Audio.MusicVolume";
    const string SoundVolumeKey  = "Audio.SoundVolume";

    public const float MinLookSensitivity = 0.25f;
    public const float MaxLookSensitivity = 2.0f;

    /// Raised after an audio preference changes so the mixer can update immediately.
    public static event System.Action AudioSettingsChanged;

    public static float LookSensitivity
    {
        get => Mathf.Clamp(PlayerPrefs.GetFloat(SensitivityKey, 1f),
                           MinLookSensitivity, MaxLookSensitivity);
        set
        {
            PlayerPrefs.SetFloat(SensitivityKey,
                Mathf.Clamp(value, MinLookSensitivity, MaxLookSensitivity));
            PlayerPrefs.Save();
        }
    }

    public static bool InvertY
    {
        get => PlayerPrefs.GetInt(InvertYKey, 0) != 0;
        set
        {
            PlayerPrefs.SetInt(InvertYKey, value ? 1 : 0);
            PlayerPrefs.Save();
        }
    }

    public static float MasterVolume
    {
        get => Mathf.Clamp01(PlayerPrefs.GetFloat(MasterVolumeKey, 0.8f));
        set
        {
            PlayerPrefs.SetFloat(MasterVolumeKey, Mathf.Clamp01(value));
            PlayerPrefs.Save();
            AudioSettingsChanged?.Invoke();
        }
    }

    public static bool MusicEnabled
    {
        get => PlayerPrefs.GetInt(MusicEnabledKey, 1) != 0;
        set
        {
            PlayerPrefs.SetInt(MusicEnabledKey, value ? 1 : 0);
            PlayerPrefs.Save();
            AudioSettingsChanged?.Invoke();
        }
    }

    public static float MusicVolume
    {
        get => Mathf.Clamp01(PlayerPrefs.GetFloat(MusicVolumeKey, 0.6f));
        set
        {
            PlayerPrefs.SetFloat(MusicVolumeKey, Mathf.Clamp01(value));
            PlayerPrefs.Save();
            AudioSettingsChanged?.Invoke();
        }
    }

    public static float SoundVolume
    {
        get => Mathf.Clamp01(PlayerPrefs.GetFloat(SoundVolumeKey, 0.8f));
        set
        {
            PlayerPrefs.SetFloat(SoundVolumeKey, Mathf.Clamp01(value));
            PlayerPrefs.Save();
            AudioSettingsChanged?.Invoke();
        }
    }

    /// Converts a raw vertical look delta to the selected direction and sensitivity.
    public static float VerticalLook(float delta) =>
        delta * LookSensitivity * (InvertY ? -1f : 1f);
}
