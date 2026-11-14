using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;

/// Lightweight coroutine-based tweens for UI Toolkit VisualElements.
public static class UITween
{
    // ── Easing ────────────────────────────────────────────────────────────────
    static float OutCubic(float t) => 1f - Mathf.Pow(1f - Mathf.Clamp01(t), 3f);
    static float InCubic(float t)  { t = Mathf.Clamp01(t); return t * t * t; }
    static float OutBack(float t)
    {
        t = Mathf.Clamp01(t);
        const float c1 = 1.70158f, c3 = c1 + 1f;
        return 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f);
    }

    // ── Style helpers (avoid deprecated transform API) ────────────────────────
    static StyleTranslate Tr(float x, float y) =>
        new StyleTranslate(new Translate(new Length(x), new Length(y)));

    static StyleScale Sc(float s) =>
        new StyleScale(new Scale(new Vector2(s, s)));

    // ── Fade ─────────────────────────────────────────────────────────────────
    public static IEnumerator FadeIn(VisualElement el, float dur, System.Action done = null)
    {
        float e = 0f;
        while (e < dur) { e += Time.deltaTime; el.style.opacity = OutCubic(e / dur); yield return null; }
        el.style.opacity = 1f;
        done?.Invoke();
    }

    public static IEnumerator FadeOut(VisualElement el, float dur, System.Action done = null)
    {
        float start = el.resolvedStyle.opacity;
        float e = 0f;
        while (e < dur) { e += Time.deltaTime; el.style.opacity = Mathf.Lerp(start, 0f, InCubic(e / dur)); yield return null; }
        el.style.opacity = 0f;
        done?.Invoke();
    }

    // ── Slide + fade (uses style.translate — no layout impact) ───────────────
    public static IEnumerator SlideIn(VisualElement el, Vector2 from, float dur)
    {
        el.style.opacity   = 0f;
        el.style.translate = Tr(from.x, from.y);
        float e = 0f;
        while (e < dur)
        {
            e += Time.deltaTime;
            float t = OutCubic(e / dur);
            el.style.translate = Tr(Mathf.Lerp(from.x, 0f, t), Mathf.Lerp(from.y, 0f, t));
            el.style.opacity   = t;
            yield return null;
        }
        el.style.translate = Tr(0f, 0f);
        el.style.opacity   = 1f;
    }

    public static IEnumerator SlideOut(VisualElement el, Vector2 to, float dur, System.Action done = null)
    {
        float e = 0f;
        while (e < dur)
        {
            e += Time.deltaTime;
            float t = InCubic(e / dur);
            el.style.translate = Tr(Mathf.Lerp(0f, to.x, t), Mathf.Lerp(0f, to.y, t));
            el.style.opacity   = 1f - t;
            yield return null;
        }
        el.style.translate = Tr(to.x, to.y);
        el.style.opacity   = 0f;
        done?.Invoke();
    }

    // ── Scale bounce in ───────────────────────────────────────────────────────
    public static IEnumerator BounceIn(VisualElement el, float dur)
    {
        el.style.opacity = 0f;
        el.style.scale   = Sc(0f);
        float e = 0f;
        while (e < dur)
        {
            e += Time.deltaTime;
            float t = e / dur;
            el.style.scale   = Sc(OutBack(t));
            el.style.opacity = Mathf.Clamp01(t * 3f);
            yield return null;
        }
        el.style.scale   = new StyleScale(new Scale(Vector2.one));
        el.style.opacity = 1f;
    }

    // ── Background color flash ────────────────────────────────────────────────
    public static IEnumerator FlashBg(VisualElement el, Color flash, float dur)
    {
        Color orig = el.resolvedStyle.backgroundColor;
        float e = 0f, half = dur * 0.5f;
        while (e < dur)
        {
            e += Time.deltaTime;
            float t = e < half ? e / half : 1f - (e - half) / half;
            el.style.backgroundColor = Color.Lerp(orig, flash, OutCubic(t));
            yield return null;
        }
        el.style.backgroundColor = orig;
    }

    // ── Text color flash ──────────────────────────────────────────────────────
    public static IEnumerator FlashText(Label el, Color flash, float dur)
    {
        Color orig = el.resolvedStyle.color;
        float e = 0f, half = dur * 0.5f;
        while (e < dur)
        {
            e += Time.deltaTime;
            float t = e < half ? e / half : 1f - (e - half) / half;
            el.style.color = Color.Lerp(orig, flash, t);
            yield return null;
        }
        el.style.color = orig;
    }

    // ── Continuous color pulse on a Label (stop coroutine to cancel) ──────────
    public static IEnumerator PulseText(Label el, Color a, Color b, float period)
    {
        float t = 0f;
        while (true)
        {
            t += Time.deltaTime;
            float v = (Mathf.Sin(t * Mathf.PI * 2f / period) + 1f) * 0.5f;
            el.style.color = Color.Lerp(a, b, v);
            yield return null;
        }
    }

    // ── Staggered entrance for a list of children ─────────────────────────────
    public static IEnumerator StaggerChildren(VisualElement container, Vector2 from,
        float dur, float stagger, MonoBehaviour host)
    {
        int i = 0;
        foreach (var child in container.Children())
        {
            float delay = i++ * stagger;
            host.StartCoroutine(DelayedSlideIn(child, from, dur, delay));
        }
        yield return null;
    }

    static IEnumerator DelayedSlideIn(VisualElement el, Vector2 from, float dur, float delay)
    {
        el.style.opacity = 0f;
        if (delay > 0f) yield return new WaitForSeconds(delay);
        yield return SlideIn(el, from, dur);
    }
}
