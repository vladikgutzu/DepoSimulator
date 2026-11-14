# Quick Spec: Extended Power Core

## Goal
Add a team-shop upgrade that lets players invest shared money into an individual robot's battery capacity.

## Player rules
- The shop sells **Extended Power Core** for **$600**.
- Each purchase adds **25 percentage points** to the purchasing robot's maximum charge.
- Maximum capacity is **200%**. Buying a core does not refill the battery.
- Stock is shared by the team: **4 per shift**, with no per-player purchase limit.
- Solo play follows the same rules, so one player may buy all four cores.
- Charging stations charge each robot up to that robot's current maximum capacity.

## UI and feedback
- The shop card displays `STOCK n/4`, then `SOLD OUT` at zero.
- The battery HUD displays current and maximum charge, for example `147% / 175%`.
- Every multiplayer client receives a notification naming the buyer and their new capacity.

## Authority and acceptance
- The server validates proximity, stock and shared funds, deducts money, decrements stock and changes only the buyer's capacity.
- A fifth purchase is rejected without spending money.
- Existing vehicle stock, deliveries and consumable purchases are unchanged.

