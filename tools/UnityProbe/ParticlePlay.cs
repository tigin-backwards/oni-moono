// Play after Stop on a looping system that still has particles ("PROBE ... PP ..."), at the main
// menu (10 s in), as Portal Network's Temperature Cube plays its fire thrower: Stop() when a
// boom ends, Play() when the next starts while the last boom's smoke still lives. Two systems
// (start delay 0.5 s and 0), rate 10, lifetime 5, loop duration 1; each plays 1.2 s, stops,
// waits 0.3 s and plays again. Every frame for 1.5 s after the second Play it records time,
// isPlaying, isEmitting and particleCount, then the same after a Play with no Stop before it.
// Then three non-looping systems (delay 0.5 s): Play while emitting, after Stop with particles
// alive, and after the duration ended with particles alive.
// With PROBE_SCENARIO=particleplay only this probe runs; it ends with a ParticlePlayProbeDone
// overlay canvas.
using System.Collections;
using System.Globalization;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SemanticsProbe
{
    public class ParticlePlay : MonoBehaviour
    {
        bool started;
        float sceneStart = -1f;

        static void L(string s) => Rec.Log("PP " + s);
        static string F(float f) => f.ToString("0.####", CultureInfo.InvariantCulture);

        void Update()
        {
            if (started || SceneManager.GetActiveScene().name != "frontend") return;
            if (sceneStart < 0f) sceneStart = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - sceneStart < 10f) return;
            started = true;
            StartCoroutine(Run());
        }

        static ParticleSystem NewSystem(string name, float delay, bool loop = true, float duration = 1f)
        {
            var go = new GameObject(name);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = loop;
            main.duration = duration;
            main.startDelay = delay;
            main.startLifetime = 5f;
            main.playOnAwake = false;
            main.maxParticles = 1000;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 10f;
            var shape = ps.shape;
            shape.enabled = false;
            return ps;
        }

        static void Row(string label, ParticleSystem ps, float since) =>
            L(label + " t=" + F(since) + " time=" + F(ps.time) + " playing=" + ps.isPlaying + " emitting=" + ps.isEmitting + " count=" + ps.particleCount);

        IEnumerator Watch(string label, ParticleSystem ps, float seconds)
        {
            float start = Time.time;
            while (Time.time - start < seconds)
            {
                Row(label, ps, Time.time - start);
                yield return null;
            }
        }

        IEnumerator Run()
        {
            L("=== particleplay");
            foreach (float delay in new[] { 0.5f, 0f })
            {
                string name = "delay" + F(delay);
                var ps = NewSystem(name, delay);
                ps.Play();
                yield return Watch(name + " first", ps, 1.2f);
                ps.Stop();
                Row(name + " stopped", ps, 0f);
                yield return Watch(name + " stopping", ps, 0.3f);
                ps.Play();
                Row(name + " replayed", ps, 0f);
                yield return Watch(name + " second", ps, 1.5f);
                ps.Play();
                Row(name + " playing again", ps, 0f);
                yield return Watch(name + " third", ps, 0.3f);
                Destroy(ps.gameObject);
            }
            // Non-looping (duration 2 s, delay 0.5 s): Play (a) while emitting, (b) after Stop
            // with particles alive, (c) after the duration ended with particles alive (duration 1).
            {
                var ps = NewSystem("once-emitting", 0.5f, false, 2f);
                ps.Play();
                yield return Watch("once-emitting first", ps, 1f);
                ps.Play();
                Row("once-emitting replayed", ps, 0f);
                yield return Watch("once-emitting second", ps, 0.8f);
                Destroy(ps.gameObject);
            }
            {
                var ps = NewSystem("once-stopped", 0.5f, false, 2f);
                ps.Play();
                yield return Watch("once-stopped first", ps, 1f);
                ps.Stop();
                yield return Watch("once-stopped stopping", ps, 0.3f);
                ps.Play();
                Row("once-stopped replayed", ps, 0f);
                yield return Watch("once-stopped second", ps, 0.8f);
                Destroy(ps.gameObject);
            }
            {
                var ps = NewSystem("once-ended", 0.5f, false, 1f);
                ps.Play();
                yield return Watch("once-ended first", ps, 1.8f);
                ps.Play();
                Row("once-ended replayed", ps, 0f);
                yield return Watch("once-ended second", ps, 0.8f);
                Destroy(ps.gameObject);
            }
            var done = new GameObject("ParticlePlayProbeDone", typeof(RectTransform));
            DontDestroyOnLoad(done);
            done.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            L("=== done");
        }
    }
}
