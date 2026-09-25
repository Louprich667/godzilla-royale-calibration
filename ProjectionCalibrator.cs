using UnityEngine;

/// <summary>
/// Calibration projecteur -> maquette physique par 4 points de couleur (homographie).
///
/// Principe :
///  - La caméra Unity regarde la maquette 3D de haut (orthographique).
///  - 4 objets vides (worldCorners) marquent les 4 coins de la maquette 3D,
///    aux mêmes endroits que les 4 gommettes de couleur sur la maquette physique.
///  - En mode calibration, le projecteur affiche 4 croix de couleur.
///    On déplace chaque croix sur la gommette physique de même couleur.
///  - Le script calcule une homographie (déformation projective) et l'injecte dans
///    la matrice de projection de la caméra : l'image 3D "colle" alors à la maquette,
///    même si le projecteur a bougé ou est de travers.
///
/// Touches :
///  C          : entrer / sortir du mode calibration (sauvegarde en sortant)
///  1,2,3,4    : choisir le coin (Rouge, Vert, Bleu, Jaune) — Tab : coin suivant
///  Flèches    : déplacer le coin (Shift = rapide, Ctrl = très fin)
///  Clic gauche: placer le coin sélectionné sous la souris (on peut glisser)
///  R          : remettre à zéro (pas de déformation)
///  Entrée     : valider et sauvegarder
/// </summary>
[RequireComponent(typeof(Camera))]
public class ProjectionCalibrator : MonoBehaviour
{
    [Tooltip("4 objets vides aux coins de la maquette 3D, dans l'ordre : Rouge, Vert, Bleu, Jaune")]
    public Transform[] worldCorners = new Transform[4];

    [Tooltip("Couleurs des croix projetées (mêmes couleurs que les gommettes physiques)")]
    public Color[] cornerColors = { Color.red, Color.green, Color.blue, Color.yellow };

    [Tooltip("Le prof veut recalibrer à chaque fois : démarre directement en mode calibration")]
    public bool calibrateOnStart = true;

    public KeyCode calibrationKey = KeyCode.C;
    public float arrowSpeedPixels = 80f;   // pixels / seconde
    public int crossSize = 40;             // taille des croix en pixels

    Camera cam;
    Matrix4x4 baseProjection;
    readonly Vector2[] targets = new Vector2[4]; // positions écran normalisées (0..1), origine en bas à gauche
    bool calibrating;
    int selected;
    Texture2D pixel;
    GUIStyle labelStyle;

    const string PrefKey = "ProjCalib_";
    static readonly string[] names = { "ROUGE", "VERT", "BLEU", "JAUNE" };

    void Start()
    {
        cam = GetComponent<Camera>();
        cam.ResetProjectionMatrix();
        baseProjection = cam.projectionMatrix;

        pixel = new Texture2D(1, 1);
        pixel.SetPixel(0, 0, Color.white);
        pixel.Apply();

        if (!Load()) ResetTargets();
        calibrating = calibrateOnStart;
    }

    void Update()
    {
        if (Input.GetKeyDown(calibrationKey))
        {
            calibrating = !calibrating;
            if (!calibrating) Save();
        }
        if (!calibrating) return;

        for (int i = 0; i < 4; i++)
            if (Input.GetKeyDown(KeyCode.Alpha1 + i) || Input.GetKeyDown(KeyCode.Keypad1 + i)) selected = i;
        if (Input.GetKeyDown(KeyCode.Tab)) selected = (selected + 1) % 4;
        if (Input.GetKeyDown(KeyCode.R)) ResetTargets();

        // Déplacement au clavier
        Vector2 dir = Vector2.zero;
        if (Input.GetKey(KeyCode.LeftArrow)) dir.x -= 1;
        if (Input.GetKey(KeyCode.RightArrow)) dir.x += 1;
        if (Input.GetKey(KeyCode.DownArrow)) dir.y -= 1;
        if (Input.GetKey(KeyCode.UpArrow)) dir.y += 1;
        float speed = arrowSpeedPixels;
        if (Input.GetKey(KeyCode.LeftShift)) speed *= 5f;
        if (Input.GetKey(KeyCode.LeftControl)) speed *= 0.15f;
        Vector2 deltaPx = dir * speed * Time.unscaledDeltaTime;
        targets[selected] += new Vector2(deltaPx.x / Screen.width, deltaPx.y / Screen.height);

        // Placement à la souris
        if (Input.GetMouseButton(0))
        {
            Vector3 m = Input.mousePosition;
            targets[selected] = new Vector2(m.x / Screen.width, m.y / Screen.height);
        }

        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            calibrating = false;
            Save();
        }
    }

    void LateUpdate()
    {
        ApplyWarp();
    }

    // ---------- Cœur du calcul ----------

    void ApplyWarp()
    {
        if (!CornersOk()) return;

        Matrix4x4 vp = baseProjection * cam.worldToCameraMatrix;
        var src = new Vector2[4];
        var dst = new Vector2[4];
        for (int i = 0; i < 4; i++)
        {
            Vector3 p = worldCorners[i].position;
            Vector4 c = vp * new Vector4(p.x, p.y, p.z, 1f);
            src[i] = new Vector2(c.x / c.w, c.y / c.w);          // où le coin tombe sans correction (NDC -1..1)
            dst[i] = targets[i] * 2f - Vector2.one;              // où il doit tomber (NDC -1..1)
        }

        double[] h = SolveHomography(src, dst);
        if (h == null) return;

        // Homographie 3x3 (en x,y,w) intégrée dans une matrice 4x4 (z laissé tel quel)
        Matrix4x4 H = Matrix4x4.identity;
        H.m00 = (float)h[0]; H.m01 = (float)h[1]; H.m02 = 0; H.m03 = (float)h[2];
        H.m10 = (float)h[3]; H.m11 = (float)h[4]; H.m12 = 0; H.m13 = (float)h[5];
        H.m20 = 0;           H.m21 = 0;           H.m22 = 1; H.m23 = 0;
        H.m30 = (float)h[6]; H.m31 = (float)h[7]; H.m32 = 0; H.m33 = 1;

        cam.projectionMatrix = H * baseProjection;
    }

    /// Résout l'homographie qui envoie src[i] -> dst[i] (4 correspondances, h22 = 1).
    public static double[] SolveHomography(Vector2[] src, Vector2[] dst)
    {
        var A = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            double x = src[i].x, y = src[i].y, u = dst[i].x, v = dst[i].y;
            int r = 2 * i;
            A[r, 0] = x; A[r, 1] = y; A[r, 2] = 1; A[r, 3] = 0; A[r, 4] = 0; A[r, 5] = 0;
            A[r, 6] = -u * x; A[r, 7] = -u * y; A[r, 8] = u;
            A[r + 1, 0] = 0; A[r + 1, 1] = 0; A[r + 1, 2] = 0; A[r + 1, 3] = x; A[r + 1, 4] = y; A[r + 1, 5] = 1;
            A[r + 1, 6] = -v * x; A[r + 1, 7] = -v * y; A[r + 1, 8] = v;
        }

        // Élimination de Gauss avec pivot partiel
        for (int col = 0; col < 8; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < 8; r++)
                if (System.Math.Abs(A[r, col]) > System.Math.Abs(A[pivot, col])) pivot = r;
            if (System.Math.Abs(A[pivot, col]) < 1e-12) return null; // 3 points alignés -> impossible
            if (pivot != col)
                for (int k = 0; k < 9; k++) { double t = A[col, k]; A[col, k] = A[pivot, k]; A[pivot, k] = t; }

            for (int r = 0; r < 8; r++)
            {
                if (r == col) continue;
                double f = A[r, col] / A[col, col];
                for (int k = col; k < 9; k++) A[r, k] -= f * A[col, k];
            }
        }

        var h = new double[8];
        for (int i = 0; i < 8; i++) h[i] = A[i, 8] / A[i, i];
        return h;
    }

    // ---------- Utilitaires ----------

    bool CornersOk()
    {
        if (worldCorners == null || worldCorners.Length != 4) return false;
        foreach (var t in worldCorners) if (t == null) return false;
        return true;
    }

    void ResetTargets()
    {
        if (!CornersOk()) return;
        Matrix4x4 vp = baseProjection * cam.worldToCameraMatrix;
        for (int i = 0; i < 4; i++)
        {
            Vector3 p = worldCorners[i].position;
            Vector4 c = vp * new Vector4(p.x, p.y, p.z, 1f);
            targets[i] = new Vector2((c.x / c.w + 1f) * 0.5f, (c.y / c.w + 1f) * 0.5f);
        }
    }

    void Save()
    {
        for (int i = 0; i < 4; i++)
        {
            PlayerPrefs.SetFloat(PrefKey + i + "x", targets[i].x);
            PlayerPrefs.SetFloat(PrefKey + i + "y", targets[i].y);
        }
        PlayerPrefs.Save();
        Debug.Log("Calibration sauvegardée.");
    }

    bool Load()
    {
        if (!PlayerPrefs.HasKey(PrefKey + "0x")) return false;
        for (int i = 0; i < 4; i++)
            targets[i] = new Vector2(PlayerPrefs.GetFloat(PrefKey + i + "x"), PlayerPrefs.GetFloat(PrefKey + i + "y"));
        return true;
    }

    /// Appelé par WebcamAutoCalibrator : impose les 4 positions écran calculées par la webcam.
    public void SetTargets(Vector2[] newTargets)
    {
        for (int i = 0; i < 4; i++) targets[i] = newTargets[i];
        calibrating = false;
        Save();
    }

    /// Applique une homographie (8 coefficients, h22 = 1) à un point.
    public static Vector2 ApplyHomography(double[] h, Vector2 p)
    {
        double w = h[6] * p.x + h[7] * p.y + 1.0;
        return new Vector2((float)((h[0] * p.x + h[1] * p.y + h[2]) / w),
                           (float)((h[3] * p.x + h[4] * p.y + h[5]) / w));
    }

    // ---------- Affichage des croix (projetées sur la maquette) ----------

    void OnGUI()
    {
        if (!calibrating) return;
        if (labelStyle == null)
        {
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
            labelStyle.normal.textColor = Color.white;
        }

        for (int i = 0; i < 4; i++)
        {
            float x = targets[i].x * Screen.width;
            float y = (1f - targets[i].y) * Screen.height; // GUI : origine en haut à gauche
            int s = (i == selected) ? crossSize * 2 : crossSize;
            int w = (i == selected) ? 5 : 3;

            GUI.color = cornerColors[i];
            GUI.DrawTexture(new Rect(x - s / 2f, y - w / 2f, s, w), pixel);
            GUI.DrawTexture(new Rect(x - w / 2f, y - s / 2f, w, s), pixel);
            if (i == selected)
            {
                // petit carré autour du coin actif
                float b = s * 0.35f;
                GUI.DrawTexture(new Rect(x - b, y - b, 2 * b, 3), pixel);
                GUI.DrawTexture(new Rect(x - b, y + b - 3, 2 * b, 3), pixel);
                GUI.DrawTexture(new Rect(x - b, y - b, 3, 2 * b), pixel);
                GUI.DrawTexture(new Rect(x + b - 3, y - b, 3, 2 * b), pixel);
            }
        }

        GUI.color = Color.white;
        GUI.Label(new Rect(20, 20, 900, 30),
            $"CALIBRATION — coin actif : {names[selected]}   |  1-4/Tab : choisir   Flèches/clic : déplacer   R : reset   Entrée : valider",
            labelStyle);
    }
}
