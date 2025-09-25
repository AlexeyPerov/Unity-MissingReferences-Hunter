using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "TestScriptableObject", menuName = "Test/Create Test Scriptable Object")]
// ReSharper disable once CheckNamespace
public class ScriptableObjectWithListAndRefs : ScriptableObject
{
    [SerializeField] private PrefabScript _prefab;
    [SerializeField] private List<PrefabScript> _array;
}