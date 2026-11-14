using System;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Vivox;
using UnityEngine;

// Place on a persistent GO in the scene. Initializes UGS and signs in anonymously
// before any lobby or relay operations are attempted.
public class NetworkBootstrapper : MonoBehaviour
{
    public static NetworkBootstrapper Instance { get; private set; }

    public bool   IsReady { get; private set; }
    public event Action OnReady;

    [Header("Vivox Credentials")]
    [SerializeField] private string _vivoxServer   = "";
    [SerializeField] private string _vivoxDomain   = "";
    [SerializeField] private string _vivoxIssuer   = "";
    [SerializeField] private string _vivoxTokenKey = "";

    private void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening)
            nm.Shutdown();
    }

    private async void Start() => await InitAsync();

    public async Task InitAsync()
    {
        if (IsReady) return;
        try
        {
            var options = new InitializationOptions();

            if (!string.IsNullOrEmpty(_vivoxServer))
                options.SetVivoxCredentials(_vivoxServer, _vivoxDomain, _vivoxIssuer, _vivoxTokenKey);

            await UnityServices.InitializeAsync(options);

            try
            {
                await VivoxService.Instance.InitializeAsync();
            }
            catch (Exception vivoxEx)
            {
                // Voice chat is optional -- a Vivox hiccup (seen intermittently across repeated
                // Play sessions in the same Editor process) shouldn't block lobby/relay readiness.
                Debug.LogWarning($"[Vivox] Init failed, voice chat unavailable this session: {vivoxEx.Message}");
            }

            string mppmProfile = GetMppmProfileSuffix();
            bool profileMismatch = !string.IsNullOrEmpty(mppmProfile)
                                 && AuthenticationService.Instance.Profile != mppmProfile;
            Debug.Log($"[UGS] MPPM profile resolved: {(mppmProfile ?? "null (not an MPPM instance, or -name arg not found)")}  " +
                      $"currentProfile={AuthenticationService.Instance.Profile}  " +
                      $"IsSignedIn={AuthenticationService.Instance.IsSignedIn}  mismatch={profileMismatch}");

            // SwitchProfile only takes effect while signed OUT — a cached session restored on
            // UnityServices.InitializeAsync() (e.g. from a run before this MPPM handling existed,
            // or from the default profile bleeding into a virtual player) would otherwise leave
            // IsSignedIn already true under the WRONG profile, so the switch below never runs and
            // this instance silently shares an identity with another one — which is exactly what
            // produces a 401 when it tries to join a lobby it isn't a legitimate member of.
            if (AuthenticationService.Instance.IsSignedIn && profileMismatch)
            {
                Debug.LogWarning($"[UGS] Signed in under profile '{AuthenticationService.Instance.Profile}' " +
                                  $"but this MPPM instance should be '{mppmProfile}' — signing out to switch.");
                AuthenticationService.Instance.SignOut();
            }

            if (!AuthenticationService.Instance.IsSignedIn)
            {
                // Anonymous sign-in is cached per machine/user, so under Multiplayer Play Mode
                // every instance would otherwise authenticate as the same player and collide
                // when joining the same Lobby ("player is already a member"). Give each MPPM
                // instance its own identity; real builds/plain solo Play are unaffected.
                if (!string.IsNullOrEmpty(mppmProfile))
                    AuthenticationService.Instance.SwitchProfile(mppmProfile);

                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            }
            IsReady = true;
            Debug.Log($"[UGS] Ready  profile={AuthenticationService.Instance.Profile}  " +
                      $"playerId={AuthenticationService.Instance.PlayerId}");
            OnReady?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[UGS] Init failed: {ex.Message}");
        }
    }

    // Returns "Player1".."Player4" if launched under a Multiplayer Play Mode scenario
    // (main Editor or a virtual player), or null for plain solo Play / a real build.
    static string GetMppmProfileSuffix()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != "-name") continue;
            string name = args[i + 1];
            if (!name.StartsWith("Player")) continue;

            // MPPM's own -name value is the virtual player's display name, e.g. "Player 2"
            // (with a space) by default in the Multiplayer Play Mode window — but Authentication
            // profile names only allow alphanumeric/'-'/'_' and reject the space, which made
            // SwitchProfile/SignInAnonymouslyAsync throw ("Invalid profile name") and silently
            // abort the whole sign-in, leaving this instance never actually authenticated.
            return name.Replace(" ", "");
        }
        return null;
    }
}
