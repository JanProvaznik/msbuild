// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Build.Collections;
using Shouldly;
using Xunit;

namespace Microsoft.Build.UnitTests.OM.Collections;

/// <summary>
/// Tests for <see cref="RetrievableEntryHashSet{T}"/>: a differential test against a dictionary,
/// plus the behaviors callers rely on (last add wins, substring lookup, insertion order, copies).
/// </summary>
public class RetrievableEntryHashSet_Tests
{
    private sealed class Entry(string key) : IKeyed
    {
        public string Key { get; } = key;

        public override string ToString() => Key;
    }

    public static IEnumerable<object[]> Comparers()
    {
        yield return [MSBuildNameIgnoreCaseComparer.Default];
        yield return [StringComparer.OrdinalIgnoreCase];
    }

    [Theory]
    [MemberData(nameof(Comparers))]
    public void MatchesDictionaryUnderRandomOperations(IEqualityComparer<string> comparer)
    {
        var random = new Random(42);
        string[] names = [.. Enumerable.Range(0, 200).Select(i => "Prop" + i), .. Enumerable.Range(0, 200).Select(i => "PROP" + i)];

        for (int round = 0; round < 50; round++)
        {
            var oracle = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            var set = new RetrievableEntryHashSet<Entry>(round % 2 == 0 ? 0 : random.Next(100), comparer);

            for (int op = 0; op < 2000; op++)
            {
                string name = names[random.Next(round % 3 == 0 ? 20 : names.Length)];
                switch (random.Next(10))
                {
                    case < 5:
                        var entry = new Entry(name);
                        set.Add(entry);
                        oracle[name] = entry;
                        break;
                    case < 7:
                        set.Remove(name).ShouldBe(oracle.Remove(name));
                        break;
                    case 7 when random.Next(100) == 0:
                        set.Clear();
                        oracle.Clear();
                        break;
                    case 8 when random.Next(50) == 0:
                        set.TrimExcess();
                        break;
                    default:
                        oracle.TryGetValue(name, out Entry? expected);
                        set.Get(name).ShouldBeSameAs(expected);
                        if (comparer is IConstrainedEqualityComparer<string>)
                        {
                            set.Get("$(" + name + ")", 2, name.Length).ShouldBeSameAs(expected);
                        }

                        break;
                }

                set.Count.ShouldBe(oracle.Count);
            }

            ((IEnumerable<Entry>)set).ShouldBe(oracle.Values, ignoreOrder: true);
        }
    }

    [Fact]
    public void LastAddWinsAndKeepsPosition()
    {
        var set = new RetrievableEntryHashSet<Entry>(MSBuildNameIgnoreCaseComparer.Default);
        var first = new Entry("Foo");
        var second = new Entry("FOO");
        set.Add(first);
        set.Add(new Entry("Bar"));
        set.Add(second);

        set.Count.ShouldBe(2);
        set.Get("foo").ShouldBeSameAs(second);
        ((IEnumerable<Entry>)set).First().ShouldBeSameAs(second);
    }

    [Fact]
    public void EnumeratesInInsertionOrderWithoutRemovals()
    {
        var set = new RetrievableEntryHashSet<Entry>(MSBuildNameIgnoreCaseComparer.Default);
        string[] names = [.. Enumerable.Range(0, 1000).Select(i => "Name" + i)];
        foreach (string name in names)
        {
            set.Add(new Entry(name));
        }

        ((IEnumerable<Entry>)set).Select(e => e.Key).ShouldBe(names);
    }

    [Fact]
    public void SubstringLookupFindsEntry()
    {
        var set = new RetrievableEntryHashSet<Entry>(MSBuildNameIgnoreCaseComparer.Default);
        var entry = new Entry("Configuration");
        set.Add(entry);

        set.Get("$(configuration)", 2, 13).ShouldBeSameAs(entry);
        set.Get("$(configuration)", 2, 12).ShouldBeNull();
    }

    [Fact]
    public void CopyIsIndependent()
    {
        var set = new RetrievableEntryHashSet<Entry>(MSBuildNameIgnoreCaseComparer.Default);
        for (int i = 0; i < 100; i++)
        {
            set.Add(new Entry("P" + i));
        }

        var copy = new RetrievableEntryHashSet<Entry>(set, MSBuildNameIgnoreCaseComparer.Default);
        copy.EntriesAreReferenceEquals(set).ShouldBeTrue();

        copy.Remove("P0").ShouldBeTrue();
        copy.Add(new Entry("New"));

        set.Count.ShouldBe(100);
        set.Get("P0").ShouldNotBeNull();
        set.Get("New").ShouldBeNull();
    }

    [Fact]
    public void ModifyingDuringEnumerationThrows()
    {
        var set = new RetrievableEntryHashSet<Entry>(MSBuildNameIgnoreCaseComparer.Default);
        set.Add(new Entry("a"));
        set.Add(new Entry("b"));

        Should.Throw<InvalidOperationException>(() =>
        {
            foreach (Entry e in set)
            {
                set.Add(new Entry("c" + e.Key));
            }
        });
    }

    [Fact]
    public void LargeSetSurvivesGrowRemoveAndTrim()
    {
        var set = new RetrievableEntryHashSet<Entry>(MSBuildNameIgnoreCaseComparer.Default);
        const int Count = 100_000;
        for (int i = 0; i < Count; i++)
        {
            set.Add(new Entry("Item" + i));
        }

        for (int i = 0; i < Count; i += 2)
        {
            set.Remove("ITEM" + i).ShouldBeTrue();
        }

        set.TrimExcess();
        set.Count.ShouldBe(Count / 2);
        for (int i = 0; i < Count; i++)
        {
            (set.Get("item" + i) is not null).ShouldBe(i % 2 == 1);
        }
    }

    [Fact]
    public void PropertyDictionaryCopyIsIndependent()
    {
        var original = new PropertyDictionary<Microsoft.Build.Execution.ProjectPropertyInstance>();
        original.Set(Microsoft.Build.Execution.ProjectPropertyInstance.Create("a", "1"));
        original.Set(Microsoft.Build.Execution.ProjectPropertyInstance.Create("b", "2"));

        var copy = new PropertyDictionary<Microsoft.Build.Execution.ProjectPropertyInstance>(original);
        copy.Set(Microsoft.Build.Execution.ProjectPropertyInstance.Create("A", "3"));
        copy.Remove("b");

        original["a"].EvaluatedValue.ShouldBe("1");
        original["b"].ShouldNotBeNull();
        copy["a"].EvaluatedValue.ShouldBe("3");
        copy["b"].ShouldBeNull();
    }
}
