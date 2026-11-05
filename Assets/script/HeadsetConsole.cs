using System.Collections.Generic;
using UnityEngine;
using TMPro;

// Console de développement visible DANS le casque.
// Récupère automatiquement tous les Debug.Log (snap, placement, guide, serrage...)
// et affiche les derniers messages sur un texte TextMeshPro (ex. attaché à la manette gauche).
// Bouton X (manette gauche) : afficher / masquer.
// Fonctionne aussi en version autonome (APK), où la Console Unity n'est pas disponible.

public class HeadsetConsole : MonoBehaviour
{
    [Header("Affichage")]
    public TMP_Text output;
    [Tooltip("Nombre de messages affichés.")]
    public int maxLines = 8;
    public bool visibleAtStart = true;

    [Header("Filtre")]
    [Tooltip("Si la liste est vide, tous les messages sont affichés. Sinon, seuls ceux qui contiennent un de ces mots.")]
    public string[] onlyContaining = { "[Placement]", "[Guide]", "[Serrage]", "[Outil]", "[Tuto]", "montage", "orientation", "endroit", "retirée", "verrouillée" };
    [Tooltip("Masquer les messages contenant 'CheckSnap APPELE' (trop fréquents).")]
    public bool hideCheckSnapCalls = true;

    [Header("Bouton")]
    public OVRInput.Button toggleButton = OVRInput.Button.Three;   // X sur la manette gauche
    public OVRInput.Controller controller = OVRInput.Controller.LTouch;

    private readonly Queue<string> lines = new Queue<string>();
    private bool visible;
    private bool dirty;

    void OnEnable() { Application.logMessageReceived += OnLog; }
    void OnDisable() { Application.logMessageReceived -= OnLog; }

    void Start()
    {
        visible = visibleAtStart;
        if (output != null)
        {
            output.richText = true;   // nécessaire pour les couleurs des messages
            output.gameObject.SetActive(visible);
        }
        dirty = true;
    }

    void Update()
    {
        if (OVRInput.GetDown(toggleButton, controller))
        {
            visible = !visible;
            if (output != null) output.gameObject.SetActive(visible);
        }

        if (dirty && output != null)
        {
            output.text = string.Join("\n", lines);
            dirty = false;
        }
    }

    private void OnLog(string message, string stackTrace, LogType type)
    {
        bool isProblem = type == LogType.Error || type == LogType.Exception || type == LogType.Assert;

        if (!isProblem)
        {
            if (hideCheckSnapCalls && message.Contains("CheckSnap APPELE")) return;
            if (onlyContaining != null && onlyContaining.Length > 0 && !ContainsAny(message)) return;
        }

        lines.Enqueue(Colorize(message, type));
        while (lines.Count > maxLines) lines.Dequeue();
        dirty = true;
    }

    private bool ContainsAny(string message)
    {
        foreach (var word in onlyContaining)
            if (!string.IsNullOrEmpty(word) && message.Contains(word)) return true;
        return false;
    }

    // Couleurs : vert = réussite, rouge = erreur de montage ou erreur Unity, jaune = avertissement.
    private string Colorize(string message, LogType type)
    {
        string time = System.DateTime.Now.ToString("HH:mm:ss");
        string color = "#FFFFFF";

        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) color = "#FF5555";
        else if (type == LogType.Warning) color = "#FFD24D";
        else if (message.Contains("correct") || message.Contains("validé") || message.Contains("verrouill")) color = "#5CFF7A";
        else if (message.Contains("mauvais") || message.Contains("MAUVAISE") || message.Contains("SUR-SERRAGE") || message.Contains("HORS SEQUENCE")) color = "#FF5555";
        else if (message.Contains("pas encore") || message.Contains("insuffisant")) color = "#FFD24D";

        return "<color=#888888>" + time + "</color> <color=" + color + ">" + message + "</color>";
    }
}