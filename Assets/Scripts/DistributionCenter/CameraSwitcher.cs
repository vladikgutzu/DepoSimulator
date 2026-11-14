using ImmersiveTraining.Management;
using UnityEngine;
using UnityEngine.UI;

namespace DistributionCenter
{
    public class CameraSwitcher : MonoBehaviour
    {
        public GameObject[] cameras;
        public Button[] switchButtons;
        public GameObject subMenu;
        public GameObject trainingSubMenu;
        public GameObject[] Light_ARR;

        private static int currentCameraIndex = 0;

        void Start()
        {
            for (int i = 0; i < switchButtons.Length; i++)
            {
                if (switchButtons[i] == null) continue;
                int index = i;
                switchButtons[i].onClick.AddListener(() => SwitchCamera(index));
            }

            if (subMenu != null)
                subMenu.SetActive(false);
        
            if (trainingSubMenu != null)
                trainingSubMenu.SetActive(true);

            SwitchCamera(0);
        }

        void SwitchCamera(int index)
        {
            if (index >= 0 && index < cameras.Length)
            {
                bool turnOffLights = index == 0 || index == 2 || index == 3;
                if (Light_ARR != null)
                {
                    foreach (GameObject lightObj in Light_ARR)
                    {
                        lightObj.SetActive(!turnOffLights);
                    }
                }

                for (int i = 0; i < cameras.Length; i++)
                {
                    if (cameras[i] != null)
                        cameras[i].SetActive(i == index);
                    if (i < switchButtons.Length && switchButtons[i] != null)
                    {
                        var img = switchButtons[i].GetComponent<Image>();
                        if (img != null) img.color = (i == index) ? Color.black : Color.white;
                    }
                }

                if (subMenu != null)
                    subMenu.SetActive(index == 1);
            
                if (trainingSubMenu != null)
                    trainingSubMenu.SetActive(index == 0);

                if (EventManager.Instance != null)
                {
                    if (index == 0 && index != currentCameraIndex)
                        EventManager.TriggerEvent(EventTypes.RESET_TIMER, gameObject);
                    else
                        EventManager.TriggerEvent(EventTypes.STOP_TIMER, gameObject);
                }

                currentCameraIndex = index;
            }
        }
    }
}
