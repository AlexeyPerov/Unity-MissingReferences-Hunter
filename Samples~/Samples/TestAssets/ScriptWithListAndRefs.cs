using System.Collections.Generic;
using UnityEngine;

// ReSharper disable once CheckNamespace
public class ScriptWithListAndRefs : MonoBehaviour
{
    [SerializeField] private PrefabScript _prefabWithRef;
    [SerializeField] private PrefabScript _prefabWithoutRef;

    [SerializeField] private List<PrefabScript> _fullArray;
    [SerializeField] private List<PrefabScript> _sparseArray;

    [SerializeField] private GameObject _internalRef;
    [SerializeField] private GameObject _internalNullRef;
}
