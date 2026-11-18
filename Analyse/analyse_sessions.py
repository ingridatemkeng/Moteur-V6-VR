# -*- coding: utf-8 -*-
"""
Analyse des sessions du simulateur moteur V6 (journal CSV de SessionLogger).

Ce que fait le script :
  1. lit tous les fichiers session_*.csv d'un dossier ;
  2. résume chaque session (temps, erreurs par type, astuces) et chaque étape ;
  3. extrait les TENTATIVES par compétence (pose de chaque sorte de pièce, serrage, ordre de serrage) ;
  4. ajuste un modèle de Knowledge Tracing bayésien (BKT) par compétence -> bkt_params.json
     (ces paramètres sont ceux de l'aide adaptative dans Unity, composant AdaptiveHelp) ;
  5. preuve de concept d'ANTICIPATION D'ERREUR : à partir de ce que l'on sait au moment où la pièce est
     attrapée, prédire si la pose va échouer (régression logistique, validation croisée) -> anticipation_model.json ;
  6. graphiques PNG.

Utilisation :
  python analyse_sessions.py <dossier_des_csv> [dossier_de_sortie] [--inclure-tests]

Les sessions du participant P00 (tests) sont ignorées, sauf avec --inclure-tests.
"""
import glob
import json
import math
import os
import re
import sys

import numpy as np
import pandas as pd
import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt

# --------------------------------------------------------------------------- compétences
# Même découpage que dans Unity (AdaptiveHelp.cs) : une compétence par sorte de pièce, plus le serrage.
SKILL_LABELS = {
    "piston": "Piston", "bielle": "Bielle", "axe": "Axe de piston", "bague": "Bagues-paliers",
    "vilebrequin": "Vilebrequin", "chapeau": "Chapeau de bielle", "vis_chc": "Vis CHC", "ecrou": "Écrous",
    "ensemble": "Ensembles piston-bielle", "carter": "Carter", "vis_carter": "Vis du carter",
    "serrage": "Serrage au couple", "ordre_serrage": "Ordre de serrage",
}

POSE_EVENTS = {"piece_posee": 1, "erreur_orientation": 0, "erreur_endroit": 0,
               "erreur_mauvaise_piece": 0, "erreur_sens_moteur": 0}


def skill_of(name):
    """Compétence d'une pièce, d'après son nom (règles identiques à AdaptiveHelp.SkillOf en C#)."""
    s = (name or "").lower()
    if "bushing" in s or "bague" in s: return "bague"
    if s.startswith("ass_pist"): return "ensemble"
    if "crank" in s: return "vilebrequin"
    if "cap_rod" in s or "cap rod" in s: return "chapeau"
    if "piston_pin" in s: return "axe"
    if "piston_rod" in s: return "bielle"
    if "piston_head" in s: return "piston"
    if "socket head" in s: return "vis_chc"
    if "vis" in s or "screw" in s or "bolt" in s: return "vis_carter"
    if "nut" in s: return "ecrou"
    if "carter" in s or "oil_pan" in s: return "carter"
    return ""


# --------------------------------------------------------------------------- lecture
def read_sessions(folder, include_tests):
    frames = []
    for path in sorted(glob.glob(os.path.join(folder, "**", "session_*.csv"), recursive=True)):
        try:
            df = pd.read_csv(path, sep=";", encoding="utf-8-sig", dtype=str, keep_default_na=False)
        except Exception as e:
            print("  ignoré (illisible) :", os.path.basename(path), e)
            continue
        if df.empty or "evenement" not in df.columns:
            continue
        df["fichier"] = os.path.basename(path)
        frames.append(df)
    if not frames:
        return pd.DataFrame()
    d = pd.concat(frames, ignore_index=True)
    for c in ["temps_s", "temps_etape_s", "duree_prise_s"]:
        d[c] = pd.to_numeric(d[c], errors="coerce")
    for c in ["etape", "erreurs_etape", "astuces_etape", "astuces_actives"]:
        d[c] = pd.to_numeric(d[c], errors="coerce").fillna(0).astype(int)
    d["valeur1_n"] = pd.to_numeric(d["valeur1"], errors="coerce")
    d["valeur2_n"] = pd.to_numeric(d["valeur2"], errors="coerce")
    d["ordre"] = d.groupby("fichier").cumcount()
    if not include_tests:
        d = d[d["participant"] != "P00"]
    # Sessions trop courtes (lancement sans montage) : ignorées.
    counts = d.groupby("fichier")["evenement"].apply(lambda e: (e == "piece_posee").sum())
    keep = counts[counts > 0].index
    return d[d["fichier"].isin(keep)].copy()


def add_trial_numbers(d):
    """Numéro d'essai de chaque participant (1er, 2e... dans l'ordre des sessions)."""
    order = d.groupby("fichier")["session"].first().reset_index()
    order = order.merge(d.groupby("fichier")["participant"].first().reset_index(), on="fichier")
    order = order.sort_values("session")
    order["essai"] = order.groupby("participant").cumcount() + 1
    return d.merge(order[["fichier", "essai"]], on="fichier")


# --------------------------------------------------------------------------- résumés
def summarize_sessions(d):
    rows = []
    for f, g in d.groupby("fichier"):
        start = g.loc[g["evenement"] == "poste_verrouille", "temps_s"]
        t0 = start.iloc[0] if len(start) else g["temps_s"].min()
        bilan = g[g["evenement"] == "bilan"]
        t_end = g.loc[g["evenement"].isin(["serrage_termine", "bilan"]), "temps_s"].max()
        err = g[g["evenement"].str.startswith("erreur_")]
        r = {
            "fichier": f, "participant": g["participant"].iloc[0], "essai": g["essai"].iloc[0],
            "astuces_actives": int(g["astuces_actives"].max()),
            "termine": len(bilan) > 0,
            "duree_montage_s": round(float(t_end - t0), 1) if not math.isnan(t_end) else None,
            "pieces_posees": int((g["evenement"] == "piece_posee").sum()),
            "erreurs": len(err),
            "astuces": int((g["evenement"] == "astuce_affichee").sum()),
        }
        for e, n in err["evenement"].value_counts().items():
            r[e] = int(n)
        rows.append(r)
    return pd.DataFrame(rows).fillna(0)


def summarize_steps(d):
    rows = []
    for (f, step), g in d[d["etape"] > 0].groupby(["fichier", "etape"]):
        piece = g["piece_etape"].iloc[0]
        rel = g[g["evenement"].isin(POSE_EVENTS)]
        rows.append({
            "fichier": f, "participant": g["participant"].iloc[0], "essai": g["essai"].iloc[0],
            "etape": step, "piece": piece, "competence": skill_of(piece) or piece,
            "duree_s": round(float(g["temps_etape_s"].max()), 2),
            "tentatives": len(rel), "erreurs": int(g["evenement"].str.startswith("erreur_").sum()),
            "astuces": int((g["evenement"] == "astuce_affichee").sum()),
        })
    return pd.DataFrame(rows)


# --------------------------------------------------------------------------- tentatives par compétence
def extract_attempts(d):
    """Une ligne par tentative observable : (participant, session, compétence, correct, contexte au moment de la prise)."""
    rows = []
    for f, g in d.sort_values(["fichier", "ordre"]).groupby("fichier"):
        participant, essai = g["participant"].iloc[0], g["essai"].iloc[0]
        errors_so_far = 0
        attempts_in_step = {}
        last_grab = {}
        records = g.to_dict("records")
        for i, r in enumerate(records):
            ev = r["evenement"]
            if ev == "piece_attrapee":
                last_grab = r
                continue
            if ev in POSE_EVENTS:
                expected = skill_of(r["piece_etape"])
                obj = skill_of(r["objet"])
                skill = expected if ev == "erreur_mauvaise_piece" else (obj or expected)
                if not skill:
                    continue
                key = (r["etape"], skill)
                attempts_in_step[key] = attempts_in_step.get(key, 0) + 1
                grab_t = last_grab.get("temps_etape_s", r["temps_etape_s"]) if last_grab else r["temps_etape_s"]
                rows.append({
                    "fichier": f, "participant": participant, "essai": essai, "temps_s": r["temps_s"],
                    "competence": skill, "correct": POSE_EVENTS[ev], "type": ev,
                    "etape": r["etape"], "tentative_etape": attempts_in_step[key],
                    "erreurs_avant": errors_so_far,
                    "astuces_etape": r["astuces_etape"], "astuces_actives": r["astuces_actives"],
                    "temps_avant_prise_s": float(grab_t) if grab_t == grab_t else 0.0,
                })
                if POSE_EVENTS[ev] == 0:
                    errors_so_far += 1
            elif ev in ("serrage_couple_valide", "erreur_sur_serrage", "erreur_couple_insuffisant", "erreur_hors_sequence"):
                if ev == "serrage_couple_valide":
                    # Une validation qui suit immédiatement un sur-serrage n'est pas une réussite.
                    prev = records[i - 1] if i > 0 else {}
                    if prev.get("evenement") == "erreur_sur_serrage" and prev.get("objet") == r["objet"]:
                        continue
                    outcomes = [("serrage", 1)]
                    if "carter" in r["piece_etape"]:
                        outcomes.append(("ordre_serrage", 1))
                elif ev == "erreur_hors_sequence":
                    outcomes = [("ordre_serrage", 0)]
                else:
                    outcomes = [("serrage", 0)]
                for skill, ok in outcomes:
                    rows.append({
                        "fichier": f, "participant": participant, "essai": essai, "temps_s": r["temps_s"],
                        "competence": skill, "correct": ok, "type": ev, "etape": r["etape"],
                        "tentative_etape": 0, "erreurs_avant": errors_so_far,
                        "astuces_etape": r["astuces_etape"], "astuces_actives": r["astuces_actives"],
                        "temps_avant_prise_s": 0.0,
                    })
                if ev.startswith("erreur_"):
                    errors_so_far += 1
    a = pd.DataFrame(rows)
    if not a.empty:
        a = a.sort_values(["participant", "fichier", "temps_s"]).reset_index(drop=True)
    return a


# --------------------------------------------------------------------------- BKT
DEFAULT_BKT = {"p_init": 0.3, "p_learn": 0.2, "p_slip": 0.1, "p_guess": 0.25}


def bkt_loglik(seqs, p_init, p_learn, p_slip, p_guess):
    ll = 0.0
    for seq in seqs:
        pl = p_init
        for ok in seq:
            p_ok = pl * (1 - p_slip) + (1 - pl) * p_guess
            p_ok = min(max(p_ok, 1e-6), 1 - 1e-6)
            ll += math.log(p_ok if ok else 1 - p_ok)
            post = pl * (1 - p_slip) / p_ok if ok else pl * p_slip / (1 - p_ok)
            pl = post + (1 - post) * p_learn
    return ll


def bkt_trace(seq, prm):
    pl, out = prm["p_init"], []
    for ok in seq:
        p_ok = pl * (1 - prm["p_slip"]) + (1 - pl) * prm["p_guess"]
        post = pl * (1 - prm["p_slip"]) / p_ok if ok else pl * prm["p_slip"] / (1 - p_ok)
        pl = post + (1 - post) * prm["p_learn"]
        out.append(pl)
    return out


def fit_bkt(attempts, min_obs=8):
    """Recherche par grille du maximum de vraisemblance, par compétence. Peu de données -> valeurs par défaut."""
    grid_init = [0.1, 0.2, 0.3, 0.5, 0.7, 0.9]
    grid_learn = [0.05, 0.1, 0.2, 0.3, 0.5]
    grid_slip = [0.05, 0.1, 0.2, 0.3]
    grid_guess = [0.05, 0.15, 0.25, 0.35]
    params = {}
    for skill in SKILL_LABELS:
        sub = attempts[attempts["competence"] == skill] if not attempts.empty else attempts
        # Séquence par participant, toutes sessions confondues (la maîtrise se conserve d'un essai à l'autre).
        seqs = [list(g["correct"]) for _, g in sub.groupby("participant")] if len(sub) else []
        n = sum(len(s) for s in seqs)
        if n < min_obs:
            params[skill] = dict(DEFAULT_BKT, n_obs=n, ajuste=False)
            continue
        best, best_ll = None, -1e18
        for a in grid_init:
            for b in grid_learn:
                for c in grid_slip:
                    for e in grid_guess:
                        ll = bkt_loglik(seqs, a, b, c, e)
                        if ll > best_ll:
                            best, best_ll = (a, b, c, e), ll
        params[skill] = {"p_init": best[0], "p_learn": best[1], "p_slip": best[2], "p_guess": best[3],
                         "n_obs": n, "ajuste": True, "log_vraisemblance": round(best_ll, 2)}
    return params


# --------------------------------------------------------------------------- anticipation d'erreur
FEATURES = ["tentative_etape", "erreurs_avant", "astuces_actives", "essai", "temps_avant_prise_s"]


def anticipation(attempts, out_dir):
    """Régression logistique : la pose va-t-elle échouer ? (informations disponibles au moment de la prise)."""
    from sklearn.linear_model import LogisticRegression
    from sklearn.metrics import roc_auc_score
    from sklearn.model_selection import GroupKFold, StratifiedKFold

    pose = attempts[attempts["competence"].isin([k for k in SKILL_LABELS if k not in ("serrage", "ordre_serrage")])].copy()
    report = {"n_tentatives": int(len(pose)), "taux_erreur": None}
    if len(pose) < 20 or pose["correct"].nunique() < 2:
        report["message"] = "Pas assez de données pour entraîner le modèle (au moins 20 tentatives, réussites et échecs)."
        return report, None
    y = 1 - pose["correct"].values                       # 1 = échec
    report["taux_erreur"] = round(float(y.mean()), 3)
    skills = sorted(pose["competence"].unique())
    X = pd.concat([pose[FEATURES].astype(float).reset_index(drop=True),
                   pd.get_dummies(pose["competence"]).reindex(columns=skills, fill_value=0).astype(float).reset_index(drop=True)], axis=1)
    X["temps_avant_prise_s"] = np.log1p(X["temps_avant_prise_s"].clip(lower=0))
    X["erreurs_avant"] = np.log1p(X["erreurs_avant"])

    groups = pose["participant"].values
    n_groups = len(set(groups))
    if n_groups >= 3:
        cv = GroupKFold(n_splits=min(5, n_groups)).split(X, y, groups)
        report["validation"] = "par participant (GroupKFold) : le modèle est testé sur des personnes qu'il n'a jamais vues"
    else:
        k = int(min(5, y.sum(), len(y) - y.sum()))
        if k < 2:
            report["message"] = "Pas assez d'échecs et de réussites pour une validation croisée."
            return report, None
        cv = StratifiedKFold(n_splits=k, shuffle=True, random_state=0).split(X, y)
        report["validation"] = "croisée stratifiée (moins de 3 participants : résultat optimiste)"

    pred, base = np.zeros(len(y)), np.zeros(len(y))
    for tr, te in cv:
        m = LogisticRegression(max_iter=1000, C=1.0, class_weight="balanced").fit(X.iloc[tr], y[tr])
        pred[te] = m.predict_proba(X.iloc[te])[:, 1]
        # Référence naïve : taux d'erreur de la compétence dans les données d'entraînement.
        rates = pd.Series(y[tr]).groupby(pose["competence"].values[tr]).mean()
        base[te] = [rates.get(s, y[tr].mean()) for s in pose["competence"].values[te]]
    try:
        report["auc_modele"] = round(float(roc_auc_score(y, pred)), 3)
        report["auc_reference_competence"] = round(float(roc_auc_score(y, base)), 3)
    except ValueError:
        report["message"] = "AUC non calculable sur ce découpage."

    final = LogisticRegression(max_iter=1000, C=1.0, class_weight="balanced").fit(X, y)
    model = {"intercept": float(final.intercept_[0]),
             "coefficients": {c: round(float(w), 4) for c, w in zip(X.columns, final.coef_[0])},
             "transformations": {"erreurs_avant": "log(1+x)", "temps_avant_prise_s": "log(1+x)"},
             "cible": "probabilité que la pose échoue"}
    with open(os.path.join(out_dir, "anticipation_model.json"), "w", encoding="utf-8") as fh:
        json.dump(model, fh, ensure_ascii=False, indent=2)
    return report, model


# --------------------------------------------------------------------------- graphiques
def plots(sessions, steps, attempts, params, out_dir):
    plt.rcParams.update({"font.size": 9, "axes.spines.top": False, "axes.spines.right": False})
    blue, orange, grey = "#2a6fdb", "#e8833a", "#9aa5b1"
    palette = [blue, orange, "#2f9e6e", "#8e5cc4", "#c0392b", grey]

    # 1. Erreurs par compétence et par essai
    if not attempts.empty:
        e = attempts.assign(echec=1 - attempts["correct"]).groupby(["competence", "essai"])["echec"].mean().unstack()
        e = e.reindex([k for k in SKILL_LABELS if k in e.index])
        fig, ax = plt.subplots(figsize=(8, 3.8))
        width = 0.8 / max(1, e.shape[1])
        for j, col in enumerate(e.columns):
            ax.bar(np.arange(len(e)) + j * width, e[col].values, width, label="essai %d" % col,
                   color=palette[j % len(palette)])
        ax.set_xticks(np.arange(len(e)) + width * (e.shape[1] - 1) / 2)
        ax.set_xticklabels([SKILL_LABELS[k] for k in e.index], rotation=35, ha="right")
        ax.set_ylabel("Taux d'échec par tentative")
        ax.set_title("Erreurs par compétence et par essai")
        ax.legend(frameon=False)
        fig.tight_layout(); fig.savefig(os.path.join(out_dir, "1_erreurs_par_competence.png"), dpi=160); plt.close(fig)

    # 2. Durée par étape (moyenne par essai)
    if not steps.empty:
        s = steps.groupby(["etape", "essai"])["duree_s"].mean().unstack()
        fig, ax = plt.subplots(figsize=(8, 3.4))
        for j, col in enumerate(s.columns):
            ax.plot(s.index, s[col].values, marker="o", ms=3, label="essai %d" % col, color=palette[j % len(palette)])
        ax.set_xlabel("Étape"); ax.set_ylabel("Durée moyenne (s)")
        ax.set_title("Temps passé par étape")
        ax.legend(frameon=False)
        fig.tight_layout(); fig.savefig(os.path.join(out_dir, "2_duree_par_etape.png"), dpi=160); plt.close(fig)

    # 3. Évolution de la maîtrise (BKT) pour chaque compétence, sur le premier participant
    if not attempts.empty:
        p = attempts["participant"].iloc[0]
        sub = attempts[attempts["participant"] == p]
        fig, ax = plt.subplots(figsize=(8, 3.6))
        for skill, g in sub.groupby("competence"):
            if len(g) < 2:
                continue
            tr = bkt_trace(list(g["correct"]), params[skill])
            ax.plot(range(1, len(tr) + 1), tr, marker="o", ms=3, label=SKILL_LABELS.get(skill, skill))
        ax.axhline(0.85, color=grey, lw=1, ls="--")
        ax.text(0.5, 0.86, "seuil de maîtrise", color=grey, fontsize=8)
        ax.set_ylim(0, 1); ax.set_xlabel("Tentative"); ax.set_ylabel("Probabilité de maîtrise")
        ax.set_title("Maîtrise estimée par le modèle (participant %s)" % p)
        ax.legend(frameon=False, fontsize=7, ncol=2)
        fig.tight_layout(); fig.savefig(os.path.join(out_dir, "3_maitrise_bkt.png"), dpi=160); plt.close(fig)

    # 4. Effet de l'aide : erreurs par session avec / sans astuces
    if not sessions.empty and sessions["astuces_actives"].nunique() > 1:
        g = sessions.groupby("astuces_actives")["erreurs"].mean()
        fig, ax = plt.subplots(figsize=(4, 3.2))
        ax.bar(["sans astuces", "avec astuces"], [g.get(0, 0), g.get(1, 0)], color=[grey, blue])
        ax.set_ylabel("Erreurs par session (moyenne)")
        ax.set_title("Effet de l'aide")
        fig.tight_layout(); fig.savefig(os.path.join(out_dir, "4_effet_aide.png"), dpi=160); plt.close(fig)


# --------------------------------------------------------------------------- programme principal
def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    include_tests = "--inclure-tests" in sys.argv
    if not args:
        print(__doc__); sys.exit(1)
    folder = args[0]
    out_dir = args[1] if len(args) > 1 else os.path.join(folder, "resultats")
    os.makedirs(out_dir, exist_ok=True)

    d = read_sessions(folder, include_tests)
    if d.empty:
        print("Aucune session exploitable dans", folder, "(les sessions P00 sont ignorées sans --inclure-tests).")
        sys.exit(0)
    d = add_trial_numbers(d)

    sessions = summarize_sessions(d)
    steps = summarize_steps(d)
    attempts = extract_attempts(d)
    params = fit_bkt(attempts)
    report, _ = anticipation(attempts, out_dir)

    sessions.to_csv(os.path.join(out_dir, "sessions.csv"), sep=";", index=False, encoding="utf-8-sig")
    steps.to_csv(os.path.join(out_dir, "etapes.csv"), sep=";", index=False, encoding="utf-8-sig")
    attempts.to_csv(os.path.join(out_dir, "tentatives.csv"), sep=";", index=False, encoding="utf-8-sig")
    with open(os.path.join(out_dir, "bkt_params.json"), "w", encoding="utf-8") as fh:
        json.dump(params, fh, ensure_ascii=False, indent=2)
    with open(os.path.join(out_dir, "anticipation_rapport.json"), "w", encoding="utf-8") as fh:
        json.dump(report, fh, ensure_ascii=False, indent=2)
    plots(sessions, steps, attempts, params, out_dir)

    print("Sessions analysées :", len(sessions), "| participants :", sessions["participant"].nunique())
    print(sessions[["participant", "essai", "astuces_actives", "termine", "duree_montage_s", "pieces_posees", "erreurs", "astuces"]].to_string(index=False))
    print("\nTentatives par compétence (taux de réussite) :")
    if not attempts.empty:
        t = attempts.groupby("competence")["correct"].agg(["count", "mean"]).rename(columns={"count": "n", "mean": "reussite"})
        print(t.round(2).to_string())
    print("\nBKT :", sum(1 for p in params.values() if p["ajuste"]), "compétence(s) ajustée(s), les autres gardent les valeurs par défaut.")
    print("Anticipation d'erreur :", json.dumps(report, ensure_ascii=False))
    print("\nRésultats écrits dans :", out_dir)


if __name__ == "__main__":
    main()
