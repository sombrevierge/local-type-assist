#!/usr/bin/env python3
import argparse
import json
import math
import os
import sqlite3
import sys
from datetime import datetime, timezone

try:
    from sklearn.feature_extraction import DictVectorizer
    from sklearn.linear_model import SGDClassifier
    from sklearn.metrics import accuracy_score
except Exception as exc:
    print(f"scikit-learn is required: {exc}", file=sys.stderr)
    raise


def normalize(value: str) -> str:
    return (value or "").strip(" ’'—-").lower().replace("ё", "е")


def suffix_bucket(length: int) -> str:
    if length <= 1:
        return "0-1"
    if length <= 3:
        return "2-3"
    if length <= 6:
        return "4-6"
    if length <= 10:
        return "7-10"
    return "11+"


def feature_names(prefix, context, candidate):
    prefix = normalize(prefix)
    candidate = normalize(candidate)
    context = [normalize(x) for x in context if normalize(x)][-5:]
    result = [
        f"candidate={candidate}",
        f"prefix={prefix}",
        f"prefix_len={min(len(prefix), 8)}",
        f"suffix_len={suffix_bucket(max(0, len(candidate) - len(prefix)))}",
    ]
    if prefix:
        result.append(f"prefix_candidate={prefix}|{candidate}")
    if len(context) >= 1:
        result.append(f"ctx1={context[-1]}")
        result.append(f"ctx1_candidate={context[-1]}|{candidate}")
    if len(context) >= 2:
        result.append(f"ctx2={context[-2]}|{context[-1]}")
    if len(context) >= 3:
        result.append(f"ctx3={context[-3]}|{context[-2]}|{context[-1]}")
    if len(context) >= 4:
        result.append(f"ctx4={context[-4]}|{context[-3]}|{context[-2]}|{context[-1]}")
    return result


def to_dict(prefix, context, candidate):
    return {name: 1.0 for name in feature_names(prefix, context, candidate)}


def expand_prefixes(word, explicit_prefix):
    explicit_prefix = normalize(explicit_prefix)
    word = normalize(word)
    if explicit_prefix and word.startswith(explicit_prefix) and len(explicit_prefix) < len(word):
        return [explicit_prefix]
    if len(word) <= 1:
        return [""]
    max_len = min(8, len(word) - 1)
    return [word[:i] for i in range(1, max_len + 1)]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--db", required=True)
    parser.add_argument("--profile", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()

    connection = sqlite3.connect(args.db)
    rows = connection.execute(
        """
        SELECT event_type, word, original_word, prefix, context_json, suggestion, weight
        FROM learning_events
        WHERE profile = ?
          AND event_type IN (
              'typed_clean', 'training_observation', 'accepted_suggestion',
              'rejected_suggestion', 'corrected_away', 'correction_target'
          )
        ORDER BY id
        """,
        (args.profile,),
    ).fetchall()
    connection.close()

    x_rows = []
    labels = []
    sample_weights = []
    positive_types = {
        "typed_clean",
        "training_observation",
        "accepted_suggestion",
        "correction_target",
    }
    negative_types = {"rejected_suggestion", "corrected_away"}

    for event_type, word, original_word, prefix, context_json, suggestion, weight in rows:
        try:
            context = json.loads(context_json or "[]")
        except Exception:
            context = []
        candidate = normalize(suggestion if event_type == "rejected_suggestion" and suggestion else word)
        if not candidate:
            continue
        label = 1 if event_type in positive_types else 0
        if event_type not in positive_types and event_type not in negative_types:
            continue
        prefixes = expand_prefixes(candidate, prefix)
        base_weight = max(1.0, float(weight or 1))
        if event_type == "training_observation":
            base_weight *= 1.7
        elif event_type == "correction_target":
            base_weight *= 2.2
        elif event_type == "corrected_away":
            base_weight *= 2.5
        elif event_type == "rejected_suggestion":
            base_weight *= 1.4

        for current_prefix in prefixes:
            x_rows.append(to_dict(current_prefix, context, candidate))
            labels.append(label)
            sample_weights.append(base_weight / math.sqrt(max(1, len(prefixes))))

    positives = sum(labels)
    negatives = len(labels) - positives
    if len(labels) < 20 or positives < 5 or negatives < 5:
        print(
            f"Недостаточно разметки для ML: {len(labels)} примеров, "
            f"положительных {positives}, отрицательных {negatives}. "
            "Продолжайте печатать, исправлять и отклонять подсказки."
        )
        return 0

    vectorizer = DictVectorizer(sparse=True, sort=True)
    x_matrix = vectorizer.fit_transform(x_rows)
    classifier = SGDClassifier(
        loss="log_loss",
        penalty="elasticnet",
        alpha=0.0008,
        l1_ratio=0.10,
        max_iter=2500,
        tol=1e-4,
        class_weight="balanced",
        random_state=42,
        average=True,
    )
    classifier.fit(x_matrix, labels, sample_weight=sample_weights)
    predictions = classifier.predict(x_matrix)
    accuracy = float(accuracy_score(labels, predictions))

    feature_names_out = vectorizer.get_feature_names_out()
    coefficients = classifier.coef_[0]
    weighted = [
        (str(name), float(value))
        for name, value in zip(feature_names_out, coefficients)
        if abs(float(value)) >= 0.002
    ]
    weighted.sort(key=lambda item: abs(item[1]), reverse=True)
    weighted = weighted[:20000]

    payload = {
        "schemaVersion": 1,
        "profile": args.profile,
        "trainedAtUtc": datetime.now(timezone.utc).isoformat(),
        "sampleCount": len(labels),
        "positiveSamples": positives,
        "intercept": float(classifier.intercept_[0]),
        "weights": dict(weighted),
        "trainingAccuracy": accuracy,
    }

    os.makedirs(os.path.dirname(args.output), exist_ok=True)
    temp = args.output + ".tmp"
    with open(temp, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, separators=(",", ":"))
    os.replace(temp, args.output)

    print(
        f"ML-модель обновлена: {len(labels)} примеров, "
        f"{len(weighted)} признаков."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
