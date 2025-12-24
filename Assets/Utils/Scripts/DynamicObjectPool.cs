using UnityEngine;
using System.Collections.Generic;

namespace MetaMultiVision.Utils.Scripts
{
    public class DynamicObjectPool : MonoBehaviour
    {
        #region Members

        [Header("Prefab & Parenting")] [SerializeField]
        private GameObject _prefab;

        [SerializeField] private Transform _poolParent; // optional; defaults to this transform

        [Header("Warmup / Minimum")] [SerializeField]
        private int _prewarm; // created on Awake

        [SerializeField] private int _minSize; // never shrink below this

        [Header("Growth")] [Tooltip("If > 1, pool expands in bursts to reduce instantiation spikes.")] [SerializeField]
        private int _expandBurst = 1;

        [Header("Optional Shrink")] [SerializeField]
        private bool _enableShrink;

        [Tooltip("How many inactive objects to keep at most (soft cap). 0 means no cap.")] [SerializeField]
        private int _maxInactive;

        [Tooltip("Shrink check interval in seconds.")] [SerializeField]
        private float _shrinkInterval = 5f;

        private readonly Queue<GameObject> _available = new();
        private readonly HashSet<GameObject> _inUse = new();

        private float _nextShrinkTime;

        public int TotalCount { get; private set; }
        public int InUseCount => _inUse.Count;
        public int AvailableCount => _available.Count;

        #endregion

        #region Unity Callbacks

        private void Awake()
        {
            if (_prefab == null)
            {
                Debug.LogError($"{nameof(DynamicObjectPool)} on {name}: Prefab is not assigned.");
                enabled = false;
                return;
            }

            if (_poolParent == null) _poolParent = transform;

            // Ensure minSize is at least prewarm if you want that behavior
            if (_minSize < 0) _minSize = 0;
            if (_prewarm < 0) _prewarm = 0;

            int initial = Mathf.Max(_prewarm, _minSize);
            CreateAndEnqueue(initial);
            _nextShrinkTime = Time.time + _shrinkInterval;
        }

        private void Update()
        {
            if (!_enableShrink) return;
            if (_shrinkInterval <= 0f) return;

            if (Time.time >= _nextShrinkTime)
            {
                ShrinkIfNeeded();
                _nextShrinkTime = Time.time + _shrinkInterval;
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Get an instance (activates it). Pool grows dynamically if needed.
        /// </summary>
        public GameObject Get(Vector3 position, Quaternion rotation)
        {
            var go = DequeueOrCreate();
            go.transform.SetPositionAndRotation(position, rotation);
            go.SetActive(true);
            _inUse.Add(go);
            return go;
        }

        /// <summary>
        /// Get an instance (activates it). Position/rotation unchanged.
        /// </summary>
        public GameObject Get()
        {
            var go = DequeueOrCreate();
            go.SetActive(true);
            _inUse.Add(go);
            return go;
        }

        /// <summary>
        /// Typed version: returns a component on the pooled object (requires prefab has T).
        /// </summary>
        public T Get<T>(Vector3 position, Quaternion rotation) where T : Component
        {
            var go = Get(position, rotation);
            if (go.TryGetComponent<T>(out var comp)) return comp;

            Debug.LogError($"{nameof(DynamicObjectPool)}: Prefab does not contain component {typeof(T).Name}.");
            // You could Release(go) here, but that may hide bugs. Prefer to fail loudly.
            return null;
        }

        /// <summary>
        /// Release an instance back to the pool (deactivates it).
        /// </summary>
        public void Release(GameObject go)
        {
            if (go == null) return; // already destroyed externally

            if (!_inUse.Remove(go))
            {
                // Either double-release or object didn't originate from this pool.
                Debug.LogWarning(
                    $"{nameof(DynamicObjectPool)}: Attempted to release an object not in use by this pool: {go.name}");
                return;
            }

            go.SetActive(false);
            go.transform.SetParent(_poolParent, worldPositionStays: false);
            _available.Enqueue(go);
        }

        /// <summary>
        /// Release via component reference.
        /// </summary>
        public void Release(Component component)
        {
            if (component == null) return;
            Release(component.gameObject);
        }

        /// <summary>
        /// Ensures at least target total instances exist.
        /// </summary>
        public void EnsureCapacity(int targetTotal)
        {
            if (targetTotal <= TotalCount) return;
            CreateAndEnqueue(targetTotal - TotalCount);
        }

        #endregion

        #region Helper Methods

        private GameObject DequeueOrCreate()
        {
            // Clean out any destroyed objects that might still be in the queue (edge case).
            while (_available.Count > 0)
            {
                var candidate = _available.Dequeue();
                if (candidate != null) return candidate;
                TotalCount = Mathf.Max(0, TotalCount - 1);
            }

            // None available: expand pool
            int toCreate = Mathf.Max(1, _expandBurst);
            CreateAndEnqueue(toCreate);

            // After creation, we should have something.
            var go = _available.Dequeue();
            return go;
        }

        private void CreateAndEnqueue(int count)
        {
            for (int i = 0; i < count; i++)
            {
                var instance = Instantiate(_prefab, _poolParent);
                instance.name = $"{_prefab.name}_Pooled";
                instance.SetActive(false);
                _available.Enqueue(instance);
                TotalCount++;
            }
        }

        private void ShrinkIfNeeded()
        {
            // Respect minSize
            int minTotal = Mathf.Max(_minSize, _prewarm);

            // If no cap, do nothing
            if (_maxInactive <= 0) return;

            // Determine how many inactive we want to keep
            int desiredInactive = Mathf.Clamp(_maxInactive, 0, int.MaxValue);

            // We can only shrink from inactive objects
            int inactiveCount = _available.Count;

            // How many can we safely destroy?
            // Total after destroying should not go below minTotal.
            int canDestroyByMin = Mathf.Max(0, TotalCount - minTotal);
            int excessInactive = Mathf.Max(0, inactiveCount - desiredInactive);

            int destroyCount = Mathf.Min(canDestroyByMin, excessInactive);
            for (int i = 0; i < destroyCount; i++)
            {
                var go = _available.Dequeue();
                if (go != null) Destroy(go);
                TotalCount = Mathf.Max(0, TotalCount - 1);
            }
        }

        #endregion
    }
}