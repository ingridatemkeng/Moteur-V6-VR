using System;
using System.Collections;
using UnityEngine;
using TMPro;

// Réglage d'une clé dynamométrique, comme en atelier : on règle la valeur de couple AVANT de serrer.
// La clé « clique » à la valeur réglée, pas à la valeur attendue par l'écrou :
// une clé mal réglée donne donc un couple insuffisant ou un sur-serrage (erreur détectée par l'écrou).
//
// À placer sur la clé, à côté du composant Tool.
// Pendant qu'on tient la clé : joystick haut / bas (n'importe quelle manette) = + / - le couple.
// La valeur s'affiche sur le panneau de consignes (via le tutoriel) : pas de texte sur la clé,
// car un texte 3D transparent fait clignoter la scène en réalité mixte.
// Chaque cran est ressenti par une petite vibration de la manette.

public class TorqueSetting : MonoBehaviour
{
    [Header("Réglage")]
    [Tooltip("Valeur au démarrage. Volontairement différente de la consigne : l'apprenant doit régler la clé.")]
    public float value = 20f;
    public float min = 5f;
    public float max = 60f;
    public float step = 1f;
    [Tooltip("Délai (s) entre deux crans quand on maintient le joystick.")]
    public float repeatDelay = 0.15f;

    [Header("Affichage (optionnel, déconseillé en réalité mixte)")]
    [Tooltip("Laisser VIDE : la valeur s'affiche sur le panneau de consignes. Un texte sur la clé fait clignoter la scène.")]
    public TMP_Text display;

    [Header("Ressenti")]
    public bool hapticTicks = true;

    // Prévient le tutoriel (panneau de consignes) à chaque cran : (clé, nouvelle valeur).
    public static event Action<TorqueSetting, float> ValueChanged;

    private Tool tool;
    private float nextStepTime;
    private bool changed;
    private float lastChangeTime;

    void Awake()
    {
        tool = GetComponent<Tool>();
        UpdateDisplay();
    }

    void Update()
    {
        if (tool == null || !tool.IsHeld) return;

        float y = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch).y
                + OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch).y;

        if (Mathf.Abs(y) > 0.5f && Time.time >= nextStepTime)
        {
            float newValue = Mathf.Clamp(value + Mathf.Sign(y) * step, min, max);
            if (!Mathf.Approximately(newValue, value))
            {
                value = newValue;
                changed = true;
                lastChangeTime = Time.time;
                UpdateDisplay();
                ValueChanged?.Invoke(this, value);
                if (hapticTicks) StartCoroutine(Tick());
            }
            nextStepTime = Time.time + repeatDelay;
        }

        // Un seul message quand on a fini de régler (évite de remplir la console).
        if (changed && Time.time - lastChangeTime > 0.6f)
        {
            changed = false;
            Debug.Log("[Outil] clé dynamométrique réglée sur " + value.ToString("F0") + " N·m.");
        }
    }

    // Petit « cran » ressenti dans les deux manettes.
    private IEnumerator Tick()
    {
        OVRInput.SetControllerVibration(1f, 0.25f, OVRInput.Controller.RTouch);
        OVRInput.SetControllerVibration(1f, 0.25f, OVRInput.Controller.LTouch);
        yield return new WaitForSeconds(0.03f);
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.LTouch);
    }

    private void UpdateDisplay()
    {
        if (display != null) display.text = value.ToString("F0") + " N·m";
    }
}
