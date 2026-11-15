using System;
using UnityEngine;

// N'affiche qu'UN seul fantôme à la fois : celui de la première pièce de la liste qui n'est pas encore posée.
// - Supprime le clignotement dû aux fantômes transparents superposés.
// - Guide l'apprenant étape par étape (embryon du futur graphe de précédence).
// - Prévient le tutoriel à chaque changement d'étape.
// - Pendant un serrage (groupe activé et pas encore terminé), aucun fantôme n'est affiché :
//   la pièce suivante n'apparaît qu'une fois le serrage fini.
// - Colore la pièce à prendre. Pour des pièces identiques et interchangeables (vis, écrous, bagues),
//   toutes celles qui restent sont colorées : l'utilisateur prend celle qu'il veut.
// À placer sur un objet de la scène (ex. gamme1). Remplir la liste dans l'ordre de la gamme.

public class GhostSequence : MonoBehaviour
{
    [Tooltip("Les pièces dans l'ordre de montage (ex. crank_shaft, piston_head, piston_pin, piston_rod, piston_cap_rod, vis, écrous).")]
    public SnapToTarget[] steps;

    [Header("Mise en évidence de la pièce à prendre")]
    public bool highlightCurrent = true;
    public Color highlightColor = new Color(1f, 0.55f, 0f);   // orange

    public SnapToTarget Current { get; private set; }
    public int CurrentIndex { get; private set; } = -1;
    public int Count => steps != null ? steps.Length : 0;

    // (pièce en cours ou null si tout est posé, index de l'étape, nombre total d'étapes)
    public event Action<SnapToTarget, int, int> StepChanged;

    private TighteningGroup[] groups = new TighteningGroup[0];
    private MaterialPropertyBlock block;
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");   // matériaux URP
    private static readonly int ColorId = Shader.PropertyToID("_Color");           // autres matériaux

    void Start()
    {
        groups = FindObjectsByType<TighteningGroup>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var g in groups)
        {
            g.onActivated.AddListener(Refresh);
            g.onAllTightened.AddListener(Refresh);
        }

        foreach (var s in steps)
        {
            if (s == null) continue;
            s.onSnapped.AddListener(Refresh);
            s.onUnsnapped.AddListener(Refresh);
        }
        Refresh();
    }

    // Masque tous les fantômes, puis affiche seulement celui de l'étape en cours.
    public void Refresh()
    {
        SnapToTarget current = null;
        int index = -1;

        for (int i = 0; i < steps.Length; i++)
        {
            var s = steps[i];
            if (s == null || s.target == null) continue;
            s.target.gameObject.SetActive(false);
            SetHighlight(s, false);
            if (current == null && !s.IsPlaced) { current = s; index = i; }
        }

        // Un serrage est en cours : on attend qu'il soit terminé avant de montrer la pièce suivante.
        bool tightening = false;
        foreach (var g in groups)
            if (g != null && g.IsActivated && !g.IsFinished) tightening = true;

        if (tightening && current != null)
        {
            current = null;
            index = -1;
            Debug.Log("[Guide] serrage en cours : la pièce suivante apparaîtra ensuite.");
        }
        else if (current != null)
        {
            current.target.gameObject.SetActive(true);
            Debug.Log("[Guide] étape en cours : " + current.name);

            // Couleur sur la pièce à prendre, et sur ses jumelles interchangeables encore à poser.
            if (highlightCurrent)
            {
                SetHighlight(current, true);
                if (current.interchangeable)
                    foreach (var s in steps)
                        if (s != null && s != current && !s.IsPlaced && s.interchangeable && s.Family == current.Family)
                            SetHighlight(s, true);
            }
        }
        else
        {
            Debug.Log("[Guide] toutes les pièces sont posées.");
        }

        bool changed = current != Current;
        Current = current;
        CurrentIndex = index;
        if (changed) StepChanged?.Invoke(current, index, steps.Length);
    }

    // Colore (ou décolore) une pièce sans toucher à son matériau.
    private void SetHighlight(SnapToTarget piece, bool on)
    {
        Renderer r = piece.GetComponent<Renderer>();
        if (r == null) return;
        if (!on) { r.SetPropertyBlock(null); return; }

        if (block == null) block = new MaterialPropertyBlock();
        block.SetColor(BaseColorId, highlightColor);
        block.SetColor(ColorId, highlightColor);
        r.SetPropertyBlock(block);
    }
}
