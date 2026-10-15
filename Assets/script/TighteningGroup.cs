using UnityEngine;
using UnityEngine.Events;

// À placer sur l'ensemble piston-bielle (ex. ass_pist_f).
// - Active les écrous seulement quand le chapeau de bielle est bien emboîté (précédence).
// - Vérifie l'ordre : toutes les vis pré-serrées au couple avant tout serrage angulaire.

public class TighteningGroup : MonoBehaviour
{
    [Header("Précédence")]
    [Tooltip("La pièce qui doit être bien placée avant le serrage (piston_cap_rod).")]
    public SnapToTarget prerequisite;

    [Header("Écrous de ce groupe")]
    public NutTightening[] nuts;

    [Header("Visserie masquée tant que le chapeau n'est pas posé (vis + écrous)")]
    public GameObject[] showOnActivate;

    [Header("Mode")]
    [Tooltip("Décoché = Apprentissage (on bloque l'action hors ordre). Coché = Évaluation (on laisse faire et on enregistre l'erreur).")]
    public bool modeEvaluation = false;

    public UnityEvent onAllTightened;

    private bool activated;
    private bool finished;

    void Awake()
    {
        foreach (var n in nuts) if (n != null) n.group = this;
        foreach (var go in showOnActivate) if (go != null) go.SetActive(false);

        if (prerequisite != null) prerequisite.onSnapped.AddListener(Activate);
        else Activate();
    }

    public void Activate()
    {
        if (activated) return;
        activated = true;
        foreach (var go in showOnActivate) if (go != null) go.SetActive(true);
        foreach (var n in nuts) if (n != null) n.Activate();
        Debug.Log("[Serrage] " + name + " : chapeau posé, serrage disponible.");
    }

    public bool AllTorqued()
    {
        foreach (var n in nuts)
            if (n != null && (n.CurrentPhase == NutTightening.Phase.Inactif || n.CurrentPhase == NutTightening.Phase.Couple))
                return false;
        return true;
    }

    public void NotifyChanged()
    {
        if (finished) return;
        foreach (var n in nuts)
            if (n != null && n.CurrentPhase != NutTightening.Phase.Termine) return;

        finished = true;
        bool anyError = false;
        foreach (var n in nuts) if (n != null && n.HadError) anyError = true;
        Debug.Log("[Serrage] " + name + " : serrage terminé" + (anyError ? " AVEC ERREUR(S)." : " sans erreur."));
        onAllTightened.Invoke();
    }
}
