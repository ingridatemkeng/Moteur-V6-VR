using System;
using UnityEngine;

// N'affiche qu'UN seul fantôme à la fois : celui de la première pièce de la liste qui n'est pas encore posée.
// - Supprime le clignotement dû aux fantômes transparents superposés.
// - Guide l'apprenant étape par étape (embryon du futur graphe de précédence).
// - Prévient le tutoriel à chaque changement d'étape.
// À placer sur un objet de la scène (ex. gamme1). Remplir la liste dans l'ordre de la gamme.

public class GhostSequence : MonoBehaviour
{
    [Tooltip("Les pièces dans l'ordre de montage (ex. crank_shaft, piston_head, piston_pin, piston_rod, piston_cap_rod, vis, écrous).")]
    public SnapToTarget[] steps;

    public SnapToTarget Current { get; private set; }
    public int CurrentIndex { get; private set; } = -1;
    public int Count => steps != null ? steps.Length : 0;

    // (pièce en cours ou null si tout est posé, index de l'étape, nombre total d'étapes)
    public event Action<SnapToTarget, int, int> StepChanged;

    void Start()
    {
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
            if (current == null && !s.IsPlaced) { current = s; index = i; }
        }

        if (current != null)
        {
            current.target.gameObject.SetActive(true);
            Debug.Log("[Guide] étape en cours : " + current.name);
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
}