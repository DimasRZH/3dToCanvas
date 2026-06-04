using UnityEngine;
using UI3D;

namespace UI3D
{
    public class UIBurstSortOverrideBehaviour : StateMachineBehaviour
    {
        [Tooltip("Name of the original 3D mesh part to override sorting for (e.g. 'Head' or 'Weapon')")]
        public string partName;
        
        [Tooltip("The Canvas sorting order to apply when the override is active. Higher numbers draw on top of UI.")]
        public int sortOrder = 1;
        
        [Tooltip("Normalized time of the animation (0.0 to 1.0) to start the override")]
        [Range(0f, 1f)]
        public float startNormalizedTime = 0.0f;
        
        [Tooltip("Normalized time of the animation (0.0 to 1.0) to end the override")]
        [Range(0f, 1f)]
        public float endNormalizedTime = 1.0f;

        private bool m_IsOverriding = false;
        private UIBurstSkinnedCharacter m_Character;

        public override void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
        {
            m_IsOverriding = false;
            m_Character = animator.GetComponent<UIBurstSkinnedCharacter>();
        }

        public override void OnStateUpdate(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
        {
            if (m_Character == null) return;

            // Wrap around for looping animations (e.g. normalizedTime of 1.2 becomes 0.2)
            float t = stateInfo.normalizedTime % 1.0f; 

            bool shouldOverride = (t >= startNormalizedTime && t <= endNormalizedTime);

            if (shouldOverride && !m_IsOverriding)
            {
                m_IsOverriding = true;
                m_Character.SetPartSortOrder(partName, sortOrder);
            }
            else if (!shouldOverride && m_IsOverriding)
            {
                m_IsOverriding = false;
                m_Character.ResetPartSortOrder(partName);
            }
        }

        public override void OnStateExit(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
        {
            if (m_IsOverriding && m_Character != null)
            {
                m_IsOverriding = false;
                m_Character.ResetPartSortOrder(partName);
            }
        }
    }
}
