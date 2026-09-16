using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace BalanceDock.Services
{
    public sealed class UsageFocusTracker
    {
        private readonly TimeSpan _idleTimeout;
        private readonly List<string> _activeStationIds = new List<string>();
        private readonly Dictionary<string, DateTime> _lastDecreaseAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        public UsageFocusTracker(TimeSpan idleTimeout)
        {
            if (idleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("idleTimeout");
            _idleTimeout = idleTimeout;
        }

        public int Count { get { return _activeStationIds.Count; } }

        public ReadOnlyCollection<string> ActiveStationIds
        {
            get { return _activeStationIds.AsReadOnly(); }
        }

        public bool Observe(string stationId, long previousQuota, long currentQuota, bool hasPreviousObservation, DateTime observedAtUtc)
        {
            if (string.IsNullOrWhiteSpace(stationId) || !hasPreviousObservation || currentQuota >= previousQuota)
                return false;

            if (_lastDecreaseAt.ContainsKey(stationId))
            {
                _lastDecreaseAt[stationId] = observedAtUtc;
                return false;
            }

            _lastDecreaseAt[stationId] = observedAtUtc;
            _activeStationIds.Add(stationId);
            return true;
        }

        public bool RemoveExpired(DateTime nowUtc)
        {
            bool changed = false;
            for (int index = _activeStationIds.Count - 1; index >= 0; index--)
            {
                string stationId = _activeStationIds[index];
                DateTime lastDecreaseAt;
                if (_lastDecreaseAt.TryGetValue(stationId, out lastDecreaseAt) && nowUtc - lastDecreaseAt <= _idleTimeout)
                    continue;

                _activeStationIds.RemoveAt(index);
                _lastDecreaseAt.Remove(stationId);
                changed = true;
            }
            return changed;
        }

        public bool Remove(string stationId)
        {
            if (string.IsNullOrWhiteSpace(stationId) || !_lastDecreaseAt.Remove(stationId)) return false;
            _activeStationIds.Remove(stationId);
            return true;
        }
    }
}
