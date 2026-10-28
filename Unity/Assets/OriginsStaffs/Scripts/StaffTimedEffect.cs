using UnityEngine;

namespace OriginsStaffs
{
    /// <summary>One-shot effect (muzzle flash, impact): fades its light out, then removes itself.</summary>
    public class StaffTimedEffect : MonoBehaviour
    {
        public float Lifetime = 2f;
        public Light FlashLight;
        public float LightFadeTime = 0.35f;

        private float m_age;
        private float m_startIntensity;

        private void Start()
        {
            if (FlashLight != null) m_startIntensity = FlashLight.intensity;
        }

        private void Update()
        {
            m_age += Time.deltaTime;
            if (FlashLight != null)
            {
                float k = LightFadeTime > 0f ? Mathf.Clamp01(1f - m_age / LightFadeTime) : 0f;
                FlashLight.intensity = m_startIntensity * k * k;
                FlashLight.enabled = k > 0f;
            }
            if (m_age >= Lifetime) Destroy(gameObject);
        }
    }
}
