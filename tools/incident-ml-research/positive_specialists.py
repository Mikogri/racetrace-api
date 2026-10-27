"""Fit-positive profile specialists; no held labels or identities in grouping."""
import numpy as np
from sklearn.cluster import KMeans
from sklearn.preprocessing import RobustScaler
from threadpoolctl import threadpool_limits
from aligned_runs import sector_matrix
from experiment import SEED


def profiles(sectors):
    shape, support, counts = [], [], []
    for values in sectors:
        rows = sector_matrix(values)
        valid = (rows[:, 12] == 0) & (rows[:, 13] == 0)
        relative = np.zeros(5)
        if valid.any():
            relative[valid] = rows[valid, 4]-np.median(rows[valid, 4])
        mask = (~valid).astype(float)
        shape.append(np.r_[relative, mask])
        support.append(mask)
        counts.append(int(valid.sum()))
    return np.array(shape), np.array(support), np.array(counts)


def partition_positives(features, measured, positive, eligible):
    eligible = np.asarray(eligible, int)
    p = eligible[np.asarray(positive, bool)[eligible]]
    if not len(p):
        raise ValueError('No fitting observed positives for specialist grouping')
    structured, shared = p[measured[p] >= 2], p[measured[p] < 2]
    scaler, clusterer = None, None
    if not len(structured):
        labels, groups = np.zeros(0, int), [np.sort(p)]
    else:
        with threadpool_limits(limits=2):
            scaler = RobustScaler().fit(features[structured])
            values = np.clip(scaler.transform(features[structured]), -4., 4.)
            count = min(3, len(np.unique(values, axis=0)))
            if count == 1:
                labels = np.zeros(len(structured), int)
            else:
                clusterer = KMeans(n_clusters=count, n_init=10, random_state=SEED).fit(values)
                labels = clusterer.labels_
        groups = [np.sort(np.r_[structured[labels == j], shared]) for j in range(count)]
    return dict(groups=groups, positive_indices=p, structured_indices=structured,
                shared_indices=shared, assignments=labels, scaler=scaler, clusterer=clusterer)
