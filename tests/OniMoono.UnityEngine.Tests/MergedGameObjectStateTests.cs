using System;
using OniMoono.Engine;
using UnityEngine;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    /// A GameObject's engine state is on the GameObject itself, merged from what was its record
    /// (docs/research/facade-merge.md, step 3): it has no record, and the values the game sees are
    /// the record's were.
    public class MergedGameObjectStateTests : EngineTest
    {
        public class Script : MonoBehaviour { }

        [Fact]
        public void ANewObjectStartsAsItsRecordDid()
        {
            var go = new GameObject("made");
            Assert.Null(go.NativeRecord);
            Assert.Equal("made", go.name);
            Assert.Equal("Untagged", go.tag);
            Assert.True(go.activeSelf);
            Assert.True(go.activeInHierarchy);
            Assert.Equal(0, go.layer);
            Assert.False(go.isStatic);
            Assert.True(go.scene.IsValid());
            Assert.Equal(HideFlags.None, go.hideFlags);
            Assert.True(go.GetInstanceID() < 0);
            Assert.Equal(-1L, go.CanvasHiddenAt);
            Assert.Equal(-1L, go.CanvasNoRaycastersAt);
            Assert.True(go.RootSlot >= 0);
            Assert.Single(go.GetComponents<Component>());
            Assert.Same(go.transform, go.Components[0]);
            Assert.Same(go.transform, go.Transform);
            Assert.Null(go.transform.EngineState);
            Assert.Same(go, go.transform.EngineGameObject);
            Assert.Same(go, ObjectTable.FindByInstanceId(go.GetInstanceID()));
            Assert.Equal("made (UnityEngine.GameObject)", go.ToString());
        }

        [Fact]
        public void TheParameterlessAndComponentConstructorsStartTheSame()
        {
            var plain = new GameObject();
            Assert.Equal("New Game Object", plain.name);
            Assert.Equal("Untagged", plain.tag);
            var with = new GameObject("with", typeof(Script));
            Assert.NotNull(with.GetComponent<Script>());
            Assert.True(with.activeSelf);
            Assert.Same(with, with.GetComponent<Script>().gameObject);
        }

        [Fact]
        public void ADestroyedObjectIsFakeNullAndHoldsNothing()
        {
            var go = new GameObject("doomed", typeof(Script));
            var child = new GameObject("child");
            child.transform.SetParent(go.transform);
            var handle = go.Handle;
            int id = go.GetInstanceID();
            Object.DestroyImmediate(go);
            Assert.True(go == null);
            Assert.Equal(0, go.GetInstanceID());
            Assert.Equal("null", go.ToString());
            Assert.Throws<NullReferenceException>(() => go.name);
            Assert.Throws<NullReferenceException>(() => go.activeSelf);
            Assert.Throws<NullReferenceException>(() => go.transform);
            Assert.Throws<NullReferenceException>(() => go.GetComponent<Script>());
            Assert.Throws<NullReferenceException>(() => go.SetActive(true));
            Assert.True(go.Destroyed);
            Assert.Empty(go.Components);
            Assert.Null(go.Transform);
            Assert.Null(ObjectTable.FindByInstanceId(id));
            Assert.Null(ObjectTable.Resolve(handle));
            Assert.False(child);
        }

        [Fact]
        public void InstantiateCopiesTheObjectsStateAsTheRecordsDid()
        {
            var go = new GameObject("source", typeof(Script));
            go.layer = 5;
            go.tag = "Untagged";
            go.isStatic = true;
            go.hideFlags = HideFlags.DontSave;
            go.SetActive(false);
            var clone = Object.Instantiate(go);
            Assert.Null(clone.NativeRecord);
            Assert.Equal("source(Clone)", clone.name);
            Assert.Equal(5, clone.layer);
            Assert.True(clone.isStatic);
            Assert.Equal(HideFlags.DontSave, clone.hideFlags);
            Assert.False(clone.activeSelf);
            Assert.Same(clone, clone.GetComponent<Script>().gameObject);
            Assert.Same(clone, clone.transform.EngineGameObject);
            Assert.NotEqual(go.GetInstanceID(), clone.GetInstanceID());
        }

        [Fact]
        public void AComponentsLinkToItsObjectIsNotAGameObjectField()
        {
            // Localization.SetFont follows GameObject-typed fields (SetFontWalkTests): the
            // component's link to its object is stored untyped.
            var field = typeof(Component).GetField("m_EngineGameObject", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field);
            Assert.Equal(typeof(object), field.FieldType);
            var go = new GameObject("holder");
            var s = go.AddComponent<Script>();
            Assert.Same(go, s.EngineGameObject);
        }
    }
}
