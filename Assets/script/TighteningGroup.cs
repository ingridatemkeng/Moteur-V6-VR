using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Serialization;

// À placer sur l'ensemble piston-bielle (ex. ass_pist_f).
// - Le serrage ne devient possible que quand TOUTES les pièces préalables sont bien posées
//   (chapeau de bielle, vis CHC, écrous) : c'est une contrainte de précédence.
// - Vérifie l'ordre de serrage : toutes les vis pré-serrées au couple avant tout serrage angulaire.

public class TighteningGroup : MonoBehaviour
{
    [Header("Précédence")]
    [Tooltip("Pièces à poser avant le serrage : piston_cap_rod, les 2 vis CHC, les 2 écrous.")]
    public SnapToTarget[] prerequisites;

    [Header("Éléments à serrer (vis CHC, ou écrous)")]
    [FormerlySerializedAs("nuts")]
    public NutTightening[] fasteners;

    [Header("Optionnel : objets masqués jusqu'à l'activation (laisser VIDE si la visserie se pose à la main)")]
    public GameObject[] showOnActivate;

    [Header("Mode")]
    [Tooltip("Décoché = Apprentissage (on bloque l'action hors ordre). Coché = Évaluation (on laisse faire et on enregistre l'erreur).")]
    public bool modeEvaluation = false;

    public UnityEvent onActivated;
    public UnityEvent onAllTightened;

    private bool activated;
    private bool finished;

    void Awake()
    {
        foreach (var f in fasteners) if (f != null) f.group = this;
        foreach (var go in showOnActivate) if (go != null) go.SetActive(false);

        if (prerequisites == null || prerequisites.Length == 0) { Activate(); return; }
        foreach (var p in prerequisites) if (p != null) p.onSnapped.AddListener(CheckPrerequisites);
    }

    private void CheckPrerequisites()
    {
        if (activated) return;
        int missing = 0;
        foreach (var p in prerequisites) if (p != null && !p.IsPlaced) missing++;

        if (missing > 0) Debug.Log("[Serrage] " + name + " : encore " + missing + " pièce(s) à poser avant le serrage.");
        else Activate();
    }

    public void Activate()
    {
        if (activated) return;
        activated = true;
        foreach (var go in showOnActivate) if (go != null) go.SetActive(true);
        foreach (var f in fasteners) if (f != null) f.Activate();
        Debug.Log("[Serrage] " + name + " : visserie en place, serrage disponible.");
        onActivated.Invoke();
    }

    public bool AllTorqued()
    {
        foreach (var f in fasteners)
            if (f != null && (f.CurrentPhase == NutTightening.Phase.Inactif || f.CurrentPhase == NutTightening.Phase.Couple))
                return false;
        return true;
    }

    public void NotifyChanged()
    {
        if (finished) return;
        foreach (var f in fasteners)
            if (f != null && f.CurrentPhase != NutTightening.Phase.Termine) return;

        finished = true;
        bool anyError = false;
        foreach (var f in fasteners) if (f != null && f.HadError) anyError = true;
        Debug.Log("[Serrage] " + name + " : serrage terminé" + (anyError ? " AVEC ERREUR(S)." : " sans erreur."));
        onAllTightened.Invoke();
    }
}