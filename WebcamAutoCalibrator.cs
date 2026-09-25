using System.Collections;
using UnityEngine;

/// <summary>
/// Calibration AUTOMATIQUE par webcam, sans aucun repère physique.
///
/// Principe :
///  - Une seule fois (touche W) : on clique dans l'image de la webcam sur 4 points
///    de la maquette (coins de rues/bâtiments au niveau du sol, bien écartés).
///    Dans Unity, les objets Coin_Rouge/Vert/Bleu/Jaune sont placés aux MÊMES points
///    sur la carte 3D.
///  - À chaque calibration (touche A, ou au démarrage) :
///      1. le projecteur passe au noir, la webcam prend une image de référence ;
///      2. il projette 4 points de couleur, un par un, à des positions connues ;
///         la webcam les détecte -> lien webcam <-> projecteur (homographie) ;
///      3. les 4 points de la maquette (vus par la webcam) sont convertis en positions
///         projecteur et envoyés à ProjectionCalibrator, qui déforme la carte.
///  - Si le projecteur bouge, on relance A : la maquette et la webcam n'ont pas bougé,
///    donc tout se recale automatiquement.
///
/// Touches :
///  W : définir les 4 points de référence dans l'image webcam (une seule fois)
///      clic gauche = placer le point suivant, Retour arrière = annuler le dernier
///  A : lancer la calibration automatique
///  C : (ProjectionCalibrator) ajustement manuel fin si besoin
///
/// IMPORTANT : fermer l'application Caméra de Windows avant (une seule appli peut
/// utiliser la webcam à la fois).
/// </summary>
public class WebcamAutoCalibrator : MonoBehaviour
{
    [Tooltip("Le script ProjectionCalibrator (sur Main Camera)")]
    public ProjectionCalibrator calibrator;

    [Tooltip("Index de la webcam (0 = première). Les noms sont affichés en mode W et dans la Console.")]
    public int webcamIndex = 0;
    public int requestedWidth = 1280;
    public int requestedHeight = 720;

    [Tooltip("Positions des 4 points projetés (0..1, origine en bas à gauche). Ils doivent tomber dans le champ de la webcam.")]
    public Vector2[] dotPositions =
    {
        new Vector2(0.30f, 0.70f), new Vector2(0.70f, 0.70f),
        new Vector2(0.70f, 0.30f), new Vector2(0.30f, 0.30f)
    };
    public Color[] dotColors = { Color.red, Color.green, Color.blue, Color.yellow };
    public float dotRadiusPixels = 40f;

    [Tooltip("Temps d'attente pour que la webcam s'adapte après chaque changement d'image (secondes)")]
    public float settleSeconds = 0.8f;

    public bool autoCalibrateOnStart = true;
    public KeyCode autoKey = KeyCode.A;
    public KeyCode setupKey = KeyCode.W;

    WebCamTexture cam;
    readonly Vector2[] refPoints = new Vector2[4]; // points de la maquette dans l'image webcam (0..1)
    bool haveRefs;

    enum Mode { Idle, Setup, Calibrating }
    Mode mode = Mode.Idle;
    int setupIndex;
    int showDot = -1;
    bool blackScreen;

    string status = "";
    float statusUntil;
    Texture2D pixel, dotTex;
    GUIStyle style;

    const string PrefKey = "WebcamRef_";
    static readonly string[] names = { "ROUGE", "VERT", "BLEU", "JAUNE" };

    void Start()
    {
        pixel = new Texture2D(1, 1);
        pixel.SetPixel(0, 0, Color.white);
        pixel.Apply();
        dotTex = MakeDisk(64);

        LoadRefs();
        StartWebcam();

        if (!haveRefs)
            SetStatus("Pas encore de points de référence : appuie sur W", 10);
        else if (autoCalibrateOnStart)
            StartCoroutine(AutoCalibrate());
    }

    void StartWebcam()
    {
        var devices = WebCamTexture.devices;
        if (devices.Length == 0)
        {
            SetStatus("Aucune webcam détectée !", 10);
            return;
        }
        for (int i = 0; i < devices.Length; i++) Debug.Log($"Webcam {i} : {devices[i].name}");
        int idx = Mathf.Clamp(webcamIndex, 0, devices.Length - 1);
        cam = new WebCamTexture(devices[idx].name, requestedWidth, requestedHeight, 30);
        cam.Play();
    }

    void OnDestroy()
    {
        if (cam != null) cam.Stop();
    }

    void Update()
    {
        if (mode == Mode.Calibrating) return;

        if (Input.GetKeyDown(setupKey))
        {
            mode = (mode == Mode.Setup) ? Mode.Idle : Mode.Setup;
            setupIndex = 0;
        }

        if (mode == Mode.Setup)
        {
            if (Input.GetMouseButtonDown(0))
            {
                Vector3 m = Input.mousePosition;
                refPoints[setupIndex] = new Vector2(m.x / Screen.width, m.y / Screen.height);
                setupIndex++;
                if (setupIndex >= 4)
                {
                    haveRefs = true;
                    SaveRefs();
                    mode = Mode.Idle;
                    SetStatus("Points de référence enregistrés. Appuie sur A pour calibrer.", 6);
                }
            }
            if (Input.GetKeyDown(KeyCode.Backspace) && setupIndex > 0) setupIndex--;
            return;
        }

        if (Input.GetKeyDown(autoKey))
        {
            if (haveRefs) StartCoroutine(AutoCalibrate());
            else SetStatus("Définis d'abord les points de référence (touche W)", 5);
        }
    }

    // ---------- Calibration automatique ----------

    IEnumerator AutoCalibrate()
    {
        if (cam == null || !cam.isPlaying)
        {
            SetStatus("Webcam non disponible (l'appli Caméra de Windows est-elle fermée ?)", 8);
            yield break;
        }
        if (calibrator == null)
        {
            SetStatus("Champ 'Calibrator' vide dans l'Inspector", 8);
            yield break;
        }

        mode = Mode.Calibrating;
        blackScreen = true;
        showDot = -1;

        // 1. Image de référence (projecteur noir)
        yield return new WaitForSeconds(settleSeconds * 1.5f);
        yield return WaitNewFrame();
        Color32[] background = cam.GetPixels32();
        int w = cam.width, h = cam.height;

        // 2. Détection des 4 points projetés, un par un
        var camPts = new Vector2[4];
        for (int i = 0; i < 4; i++)
        {
            showDot = i;
            yield return new WaitForSeconds(settleSeconds);
            yield return WaitNewFrame();
            Color32[] frame = cam.GetPixels32();
            if (!FindSpot(background, frame, w, h, out camPts[i]))
            {
                EndCalibration();
                SetStatus($"Point {names[i]} non vu par la webcam. Rapproche les points du centre (Dot Positions) ou baisse la lumière.", 10);
                yield break;
            }
        }
        EndCalibration();

        // 3. Homographie webcam -> écran projecteur, puis conversion des points de la maquette
        double[] H = ProjectionCalibrator.SolveHomography(camPts, dotPositions);
        if (H == null)
        {
            SetStatus("Calcul impossible (points alignés ?). Réessaie.", 8);
            yield break;
        }
        var targets = new Vector2[4];
        for (int k = 0; k < 4; k++) targets[k] = ProjectionCalibrator.ApplyHomography(H, refPoints[k]);
        calibrator.SetTargets(targets);

        SetStatus("Calibration automatique terminée", 4);
    }

    void EndCalibration()
    {
        showDot = -1;
        blackScreen = false;
        mode = Mode.Idle;
    }

    IEnumerator WaitNewFrame()
    {
        float t = 0f;
        do
        {
            yield return null;
            t += Time.unscaledDeltaTime;
        } while (!cam.didUpdateThisFrame && t < 2f);
    }

    /// Trouve le centre de la tache lumineuse apparue entre l'image de fond et l'image actuelle.
    static bool FindSpot(Color32[] bg, Color32[] fr, int w, int h, out Vector2 uv)
    {
        uv = Vector2.zero;
        if (bg.Length != fr.Length || fr.Length != w * h) return false;

        const int step = 2;
        int maxD = 0;
        for (int y = 0; y < h; y += step)
            for (int x = 0; x < w; x += step)
            {
                int i = y * w + x;
                int d = (fr[i].r + fr[i].g + fr[i].b) - (bg[i].r + bg[i].g + bg[i].b);
                if (d > maxD) maxD = d;
            }
        if (maxD < 60) return false; // rien de visible

        int thr = maxD / 2;
        double sx = 0, sy = 0, sw = 0;
        int count = 0;
        for (int y = 0; y < h; y += step)
            for (int x = 0; x < w; x += step)
            {
                int i = y * w + x;
                int d = (fr[i].r + fr[i].g + fr[i].b) - (bg[i].r + bg[i].g + bg[i].b);
                if (d > thr)
                {
                    sx += (x + 0.5) * d;
                    sy += (y + 0.5) * d;
                    sw += d;
                    count++;
                }
            }
        if (count < 3) return false;
        uv = new Vector2((float)(sx / sw / w), (float)(sy / sw / h));
        return true;
    }

    // ---------- Sauvegarde ----------

    void SaveRefs()
    {
        for (int i = 0; i < 4; i++)
        {
            PlayerPrefs.SetFloat(PrefKey + i + "x", refPoints[i].x);
            PlayerPrefs.SetFloat(PrefKey + i + "y", refPoints[i].y);
        }
        PlayerPrefs.Save();
    }

    void LoadRefs()
    {
        haveRefs = PlayerPrefs.HasKey(PrefKey + "0x");
        if (!haveRefs) return;
        for (int i = 0; i < 4; i++)
            refPoints[i] = new Vector2(PlayerPrefs.GetFloat(PrefKey + i + "x"), PlayerPrefs.GetFloat(PrefKey + i + "y"));
    }

    // ---------- Affichage ----------

    void SetStatus(string msg, float seconds)
    {
        status = msg;
        statusUntil = Time.time + seconds;
        Debug.Log(msg);
    }

    void OnGUI()
    {
        GUI.depth = -10; // au-dessus du reste
        if (style == null)
        {
            style = new GUIStyle(GUI.skin.box) { fontSize = 18, alignment = TextAnchor.MiddleLeft };
            style.normal.textColor = Color.white;
        }
        Rect full = new Rect(0, 0, Screen.width, Screen.height);

        if (mode == Mode.Setup && cam != null)
        {
            GUI.color = Color.white;
            GUI.DrawTexture(full, cam, ScaleMode.StretchToFill);
            for (int i = 0; i < setupIndex; i++) DrawDot(refPoints[i], dotColors[i], 12);
            GUI.color = Color.white;
            GUI.Box(new Rect(10, 10, Screen.width - 20, 60),
                $"  Webcam : {cam.deviceName}\n  Clique sur le point {names[setupIndex]} (le même que Coin_{Cap(names[setupIndex])} dans Unity)   —   Retour arrière : annuler", style);
        }

        if (blackScreen)
        {
            GUI.color = Color.black;
            GUI.DrawTexture(full, pixel);
            if (showDot >= 0) DrawDot(dotPositions[showDot], dotColors[showDot], dotRadiusPixels);
        }

        if (Time.time < statusUntil && !blackScreen)
        {
            GUI.color = Color.white;
            GUI.Box(new Rect(10, Screen.height - 50, Screen.width - 20, 40), "  " + status, style);
        }
    }

    void DrawDot(Vector2 norm, Color c, float r)
    {
        float x = norm.x * Screen.width;
        float y = (1f - norm.y) * Screen.height;
        GUI.color = c;
        GUI.DrawTexture(new Rect(x - r, y - r, 2 * r, 2 * r), dotTex);
    }

    static string Cap(string s) => s.Substring(0, 1) + s.Substring(1).ToLower();

    static Texture2D MakeDisk(int size)
    {
        var t = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float c = (size - 1) / 2f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                t.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(c - d)));
            }
        t.Apply();
        return t;
    }
}
