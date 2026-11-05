using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Events;

// À placer sur une pièce attrapable (ex. piston_head).
// Au relâchement, vérifie si la pièce est posée à la bonne POSITION et dans la bonne ORIENTATION.
// C'est la brique de base de l'emboîtement ET de la détection d'erreurs.
// V3 : le fantôme (cible) disparaît quand la pièce est bien placée, et RÉAPPARAÎT si on la retire.
//
// Câblage (PointableUnityEventWrapper de la pièce) :
//   When Select   -> SnapToTarget.OnGrabbed()
//   When Unselect -> SnapToTarget.CheckSnap()

public class SnapToTarget : MonoBehaviour
{
    [Header("Cible (pose assemblée correcte)")]
    public Transform target;                 // le fantôme placé à la position/orientation finale

    [Header("Tolérances")]
    public float positionTolerance = 0.03f;  // 3 cm
    public float angleTolerance = 15f;       // 15 degrés

    public enum Symmetry
    {
        Aucune,                 // la rotation complète compte (piston, bielle, chapeau, vilebrequin)
        RotationAutourAxe,      // la pièce peut tourner librement autour de son axe
        AxeEtRetournement       // idem, et elle peut aussi être montée retournée bout pour bout (axe de piston)
    }

    [Header("Symétrie (pièces cylindriques)")]
    [Tooltip("Aucune = rotation complète vérifiée. Pour piston_pin : Axe Et Retournement.")]
    public Symmetry symmetry = Symmetry.Aucune;
    public enum AxisShape { PlusLongue, PlusFine }
    [Tooltip("Coché : l'axe de symétrie est trouvé automatiquement à partir de la forme de la pièce.")]
    public bool autoDetectSymmetryAxis = true;
    [Tooltip("Plus Longue : vis, axe de piston (l'axe suit la longueur). Plus Fine : écrou, rondelle (l'axe traverse l'épaisseur).")]
    public AxisShape symmetryAxisShape = AxisShape.PlusLongue;
    [Tooltip("Axe de symétrie dans le repère LOCAL de la pièce : (1,0,0)=X rouge, (0,1,0)=Y vert, (0,0,1)=Z bleu.")]
    public Vector3 symmetryAxis = Vector3.up;

    [Header("Pièces identiques (vis, écrous, pistons...)")]
    [Tooltip("Coché : une pièce identique peut aller sur l'emplacement d'une autre (les deux échangent leurs emplacements). À décocher pour les pièces appairées (ex. chapeau de bielle, propre à sa bielle).")]
    public bool interchangeable = true;
    [Tooltip("Famille de la pièce. Vide = déduite du nom (« hex nut… » et « hex nut….001 » sont de la même famille).")]
    public string partFamily = "";

    [Header("Guidage en direct")]
    [Tooltip("Pendant qu'on tient la pièce près de sa cible, affiche la distance et l'écart d'angle dans la console.")]
    public bool liveGuidance = true;
    [Tooltip("Distance (m) à partir de laquelle le guidage s'affiche.")]
    public float guidanceDistance = 0.10f;

    [Header("Fantôme")]
    [Tooltip("Masque le fantôme quand la pièce est bien placée, le réaffiche si on la retire.")]
    public bool hideTargetWhenPlaced = true;

    [Header("Verrouillage")]
    [Tooltip("Coché (scénario montage) : une pièce bien posée ne peut plus être déplacée. Décoché : elle peut être retirée.")]
    public bool lockWhenPlaced = true;
    [Tooltip("Facultatif : l'objet de préhension à désactiver. Vide = trouvé automatiquement (enfant dont le nom contient 'GrabInteraction').")]
    public GameObject grabInteraction;

    [Header("Réactions (vibration, texte, guide 3D...)")]
    public UnityEvent onSnapped;             // montage correct
    public UnityEvent onWrongOrientation;    // bon endroit, mauvaise orientation
    public UnityEvent onWrongPlace;          // pas au bon endroit
    public UnityEvent onUnsnapped;           // pièce retirée après avoir été bien posée
    public UnityEvent onGrab;                // pièce attrapée (utilisé par le tutoriel)
    public UnityEvent onWrongPiece;          // posée sur l'emplacement d'une AUTRE sorte de pièce

    public bool IsPlaced => isPlaced;

    private Rigidbody rb;
    private bool isPlaced = false;
    private bool isHeld = false;
    private float nextGuidanceTime;

    // Toutes les pièces de la scène, pour reconnaître un emplacement occupé par la mauvaise pièce.
    private static readonly List<SnapToTarget> all = new List<SnapToTarget>();
    void OnEnable() { if (!all.Contains(this)) all.Add(this); }
    void OnDestroy() { all.Remove(this); }

    // Famille : le nom sans les suffixes de copie (« .001 », « (1) », « _Target »...).
    public string Family
    {
        get
        {
            if (!string.IsNullOrEmpty(partFamily)) return partFamily;
            string n = Regex.Replace(name, @"(\.\d+|\s*\(\d+\))+$", "");
            return n.Trim().ToLowerInvariant();
        }
    }

    void Awake()
    {
        rb = GetComponent<Rigidbody>();

        // Recherche automatique de l'enfant de préhension créé par la Quick Action (ISDK_HandGrabInteraction).
        if (grabInteraction == null)
        {
            foreach (Transform child in transform)
            {
                if (child.name.Contains("GrabInteraction")) { grabInteraction = child.gameObject; break; }
            }
        }

        // Axe de symétrie trouvé à partir des dimensions du maillage.
        if (symmetry != Symmetry.Aucune && autoDetectSymmetryAxis)
        {
            MeshFilter mf = GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                Vector3 s = mf.sharedMesh.bounds.size;
                bool longest = symmetryAxisShape == AxisShape.PlusLongue;
                if (longest ? (s.x >= s.y && s.x >= s.z) : (s.x <= s.y && s.x <= s.z)) symmetryAxis = Vector3.right;
                else if (longest ? (s.y >= s.x && s.y >= s.z) : (s.y <= s.x && s.y <= s.z)) symmetryAxis = Vector3.up;
                else symmetryAxis = Vector3.forward;
            }
        }
    }

    // Guidage en direct : distance et écart d'angle pendant qu'on approche la pièce de sa cible.
    void Update()
    {
        if (!liveGuidance || !isHeld || isPlaced || target == null || Time.time < nextGuidanceTime) return;
        nextGuidanceTime = Time.time + 0.5f;

        float distance = Vector3.Distance(transform.position, target.position);
        if (distance > guidanceDistance) return;

        float angle = OrientationError();
        bool posOk = distance <= positionTolerance;
        bool angOk = angle <= angleTolerance;
        string verdict = (posOk && angOk) ? "OK, relâchez." : (!angOk ? "tournez la pièce." : "rapprochez-la.");
        Debug.Log("[Guide] " + name + " : " + (distance * 100f).ToString("F0") + " cm, écart "
                  + angle.ToString("F0") + "° — " + verdict);
    }

    // Pour un futur scénario (démontage, reprise) : rend la pièce de nouveau attrapable.
    public void Unlock()
    {
        if (grabInteraction != null) grabInteraction.SetActive(true);
    }

    // À appeler quand on attrape la pièce (When Select).
    // Si la pièce était posée, on la « désemboîte » : elle redevient à placer et le fantôme réapparaît.
    public void OnGrabbed()
    {
        isHeld = true;
        onGrab.Invoke();
        if (!isPlaced) return;

        isPlaced = false;
        if (hideTargetWhenPlaced && target != null) target.gameObject.SetActive(true);
        Debug.Log(name + " : pièce retirée de sa position.");
        onUnsnapped.Invoke();
    }

    // À appeler au relâchement de la pièce (When Unselect).
    public void CheckSnap()
    {
        isHeld = false;
        Debug.Log(name + " : CheckSnap APPELE");
        if (isPlaced || target == null) return;

        float distance = Vector3.Distance(transform.position, target.position);
        float angle = OrientationError();

        // Cas 1 : bon endroit ET bonne orientation -> montage correct
        if (distance <= positionTolerance && angle <= angleTolerance)
        {
            transform.position = target.position;   // on cale exactement en place
            transform.rotation = target.rotation;
            if (rb != null) rb.isKinematic = true;
            isPlaced = true;

            if (hideTargetWhenPlaced) target.gameObject.SetActive(false);

            Debug.Log(name + " : montage correct.");
            onSnapped.Invoke();

            // Verrouillage optionnel, à la frame suivante
            // (pas pendant l'événement de relâchement lui-même, pour éviter les conflits avec l'Interaction SDK).
            if (lockWhenPlaced)
            {
                if (grabInteraction != null) StartCoroutine(DisableGrabNextFrame());
                else Debug.LogWarning(name + " : verrouillage impossible, objet de préhension introuvable.");
            }
        }
        // Cas 2bis : la pièce est sur l'emplacement d'une AUTRE pièce
        else if (distance > positionTolerance && CheckOtherSlots()) { }
        // Cas 2 : bon endroit MAIS mauvaise orientation -> erreur d'orientation
        else if (distance <= positionTolerance)
        {
            Debug.Log(name + " : mauvaise orientation (" + angle.ToString("F0") + " deg).");
            onWrongOrientation.Invoke();
        }
        // Cas 3 : pas au bon endroit
        else
        {
            Debug.Log(name + " : pas encore au bon endroit.");
            onWrongPlace.Invoke();
        }
    }

    // Vérifie si la pièce a été relâchée sur l'emplacement d'une autre pièce encore libre.
    // - Même famille et interchangeable : on échange les emplacements, puis on vérifie la pose normalement.
    // - Autre famille : erreur « mauvaise pièce ».
    // Renvoie true si la situation a été traitée.
    private bool CheckOtherSlots()
    {
        SnapToTarget other = null;
        float best = positionTolerance;
        foreach (var p in all)
        {
            if (p == this || p == null || p.isPlaced || p.target == null) continue;
            float d = Vector3.Distance(transform.position, p.target.position);
            if (d <= best) { best = d; other = p; }
        }
        if (other == null) return false;

        if (interchangeable && other.interchangeable && other.Family == Family)
        {
            // Pièces identiques : chacune prend l'emplacement de l'autre.
            Transform mine = target;
            target = other.target;
            other.target = mine;
            Debug.Log(name + " : emplacement de " + other.name + " utilisé (pièces identiques).");
            if (!other.target.gameObject.activeSelf && target.gameObject.activeSelf)
            {
                // On garde l'affichage des fantômes cohérent.
                other.target.gameObject.SetActive(true);
                target.gameObject.SetActive(false);
            }
            isHeld = false;
            CheckSnap();            // nouvelle vérification avec la bonne cible
            return true;
        }

        Debug.Log(name + " : MAUVAISE PIÈCE, pas le bon endroit pour elle : cet emplacement attend « " + other.name + " ».");
        onWrongPiece.Invoke();
        return true;
    }

    // Écart d'orientation (en degrés) entre la pièce et sa cible, en tenant compte de la symétrie.
    // Pour une pièce symétrique, on ne compare que la direction de son axe :
    // la rotation autour de l'axe (et éventuellement le retournement) n'est pas une erreur.
    private float OrientationError()
    {
        if (symmetry == Symmetry.Aucune)
            return Quaternion.Angle(transform.rotation, target.rotation);

        Vector3 a = transform.TransformDirection(symmetryAxis).normalized;
        Vector3 b = target.TransformDirection(symmetryAxis).normalized;
        float angle = Vector3.Angle(a, b);

        if (symmetry == Symmetry.AxeEtRetournement)
            angle = Mathf.Min(angle, 180f - angle);

        return angle;
    }

    private IEnumerator DisableGrabNextFrame()
    {
        yield return null;
        grabInteraction.SetActive(false);
        Debug.Log(name + " : pièce verrouillée.");
    }
}