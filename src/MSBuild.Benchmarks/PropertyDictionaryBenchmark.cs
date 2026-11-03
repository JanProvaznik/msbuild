// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using BenchmarkDotNet.Attributes;
using Microsoft.Build.Collections;
using Microsoft.Build.Execution;

namespace MSBuild.Benchmarks;

/// <summary>
/// Exercises <see cref="PropertyDictionary{T}"/> the way evaluation and the scheduler do:
/// substring lookups of <c>$(Name)</c> references, populating a table, enumerating it, and copying it
/// (configuration metadata, MSBuild task global properties, project instance copies).
/// </summary>
[MemoryDiagnoser]
public class PropertyDictionaryBenchmark
{
    [Params(64, 512)]
    public int PropertyCount { get; set; }

    private ProjectPropertyInstance[] _properties = [];
    private string[] _references = [];
    private PropertyDictionary<ProjectPropertyInstance> _dictionary = new();

    [GlobalSetup]
    public void Setup()
    {
        _properties = new ProjectPropertyInstance[PropertyCount];
        for (int i = 0; i < PropertyCount; i++)
        {
            _properties[i] = ProjectPropertyInstance.Create($"SomeProperty_{i}_Name", "value");
        }

        // Two references per defined property (one with different casing) plus one miss for every two hits.
        var random = new Random(1);
        _references = new string[PropertyCount * 3];
        for (int i = 0; i < _references.Length; i++)
        {
            int n = random.Next(PropertyCount);
            _references[i] = (i % 3) switch
            {
                0 => $"$(SomeProperty_{n}_Name)",
                1 => $"$(SOMEPROPERTY_{n}_NAME)",
                _ => $"$(Undefined_{n}_Name)",
            };
        }

        _dictionary = new PropertyDictionary<ProjectPropertyInstance>();
        foreach (ProjectPropertyInstance property in _properties)
        {
            _dictionary.Set(property);
        }
    }

    [Benchmark]
    public int Lookup()
    {
        int hits = 0;
        foreach (string reference in _references)
        {
            if (_dictionary.GetProperty(reference, 2, reference.Length - 2) is not null)
            {
                hits++;
            }
        }

        return hits;
    }

    [Benchmark]
    public int Populate()
    {
        var dictionary = new PropertyDictionary<ProjectPropertyInstance>();
        foreach (ProjectPropertyInstance property in _properties)
        {
            dictionary.Set(property);
        }

        return dictionary.Count;
    }

    [Benchmark]
    public int Enumerate()
    {
        int length = 0;
        foreach (ProjectPropertyInstance property in _dictionary)
        {
            length += property.Name.Length;
        }

        return length;
    }

    [Benchmark]
    public int Copy() => new PropertyDictionary<ProjectPropertyInstance>(_dictionary).Count;
}
