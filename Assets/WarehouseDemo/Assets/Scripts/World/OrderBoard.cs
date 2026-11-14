using UnityEngine;

public class OrderBoard : MonoBehaviour, IInteractable
{
    public string InteractPrompt
    {
        get
        {
            bool isMP = Unity.Netcode.NetworkManager.Singleton?.IsListening ?? false;
            if (isMP)
            {
                var cgm = CoopGameManager.Instance;
                if (cgm == null) return "Order board";
                string name = cgm.ActiveOrderName.Value.ToString();
                return name.Length > 0
                    ? $"Order board  {InputHints.E}  (active: {name})"
                    : $"Order board  {InputHints.E}";
            }

            var gm = GameManager.Instance;
            if (gm == null) return "Order board";
            if (gm.DemoComplete) return "Demo complete";
            if (gm.ActiveOrder != null) return $"Order board  {InputHints.E}  (active: {gm.ActiveOrder.OrderName})";
            return gm.IsFinalOrderWindow
                ? $"Order board  {InputHints.E}  (take your final order)"
                : $"Order board  {InputHints.E}";
        }
    }

    public void Interact(GameObject interactor)
    {
        HUDController.Instance?.ShowOrderBoard();
    }
}
