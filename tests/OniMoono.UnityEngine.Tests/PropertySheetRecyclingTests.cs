using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// Property sheets make their stores at the first write, and a block's sheet that a queued
    /// draw shares is recycled, but only once the draws of the epoch after the one it was shared
    /// in are done.
    public class PropertySheetRecyclingTests : EngineTest
    {
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int ScaleId = Shader.PropertyToID("_Scale");

        [Fact]
        public void ANewSheetReadsEmptyAndMakesOnlyWhatIsWritten()
        {
            var sheet = new PropertySheet();
            Assert.True(sheet.IsEmpty);
            Assert.Empty(sheet.Floats);
            Assert.False(sheet.Textures.ContainsKey(ColorId));
            sheet.SetVector(ColorId, Vector4.one);
            Assert.False(sheet.IsEmpty);
            Assert.Equal(Vector4.one, sheet.Vectors[ColorId]);
            // The other stores are still the shared empty ones, and still empty.
            Assert.Empty(sheet.Floats);
            Assert.Empty(new PropertySheet().Vectors);
            sheet.Clear();
            Assert.True(sheet.IsEmpty);
        }

        [Fact]
        public void ACopyHasTheValuesAndArraysOfItsOwn()
        {
            var a = new PropertySheet();
            a.SetFloat(ScaleId, 2f);
            a.SetVectorArray(ColorId, [Vector4.one, Vector4.zero], 2);
            var b = new PropertySheet();
            b.SetInt(ScaleId, 5);
            b.CopyFrom(a);
            Assert.Equal(2f, b.Floats[ScaleId]);
            Assert.Empty(b.Ints);
            Assert.NotSame(a.VectorArrays[ColorId], b.VectorArrays[ColorId]);
            Assert.Equal(a.VectorArrays[ColorId], b.VectorArrays[ColorId]);
        }

        static void EndFrame()
        {
            PropertySheet.RenderEpoch++;
            PropertySheet.EndEpoch();
        }

        [Fact]
        public void AQueuedDrawKeepsTheValuesItWasGivenUntilTheSheetIsFree()
        {
            var mesh = new Mesh { vertices = [Vector3.zero, Vector3.up, Vector3.right], triangles = [0, 1, 2] };
            var block = new MaterialPropertyBlock();
            block.SetColor(ColorId, Color.red);
            var shared = block.Sheet;
            Graphics.DrawMesh(mesh, Matrix4x4.identity, null, 0, null, 0, block);
            // A change after the draw moves the block to another sheet; the draw's keeps red.
            block.SetColor(ColorId, Color.blue);
            Assert.NotSame(shared, block.Sheet);
            Assert.Equal((Vector4)Color.red, shared.Vectors[ColorId]);
            // Not handed out while the next epoch's draws may still read it.
            EndFrame();
            for (int i = 0; i < 64; i++) Assert.NotSame(shared, PropertySheet.Rent());
            Assert.Equal((Vector4)Color.red, shared.Vectors[ColorId]);
            EndFrame();
            // Free now, and cleared when it is handed out again.
            bool found = false;
            for (int i = 0; i < 4096 && !found; i++)
            {
                var sheet = PropertySheet.Rent();
                if (!ReferenceEquals(sheet, shared)) continue;
                found = true;
                Assert.True(sheet.IsEmpty);
                Assert.Equal(-1, sheet.SharedEpoch);
            }
            Assert.True(found, "the retired sheet was never handed out again");
            Assert.Equal((Vector4)Color.blue, block.GetVector(ColorId));
            Object.DestroyImmediate(mesh);
        }

        [Fact]
        public void ABlockCopiedEveryDrawAllocatesNoSheetsOnceWarm()
        {
            var block = new MaterialPropertyBlock();
            void Frame()
            {
                // As the game's light buffer: one block, set and drawn for each light.
                for (int i = 0; i < 8; i++)
                {
                    block.SetColor(ColorId, new Color(i, 0f, 0f, 1f));
                    block.SetFloat(ScaleId, i);
                    // What Graphics.DrawMesh does to the block (its queue is not drawn here).
                    block.Sheet.SharedEpoch = PropertySheet.RenderEpoch;
                }
                EndFrame();
            }
            for (int i = 0; i < 6; i++) Frame();
            long before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 6; i++) Frame();
            long bytes = System.GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(bytes < 2048, $"{bytes} bytes for 48 copies");
        }
    }
}
