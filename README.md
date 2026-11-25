# Market Notes companion

Windows app for [osmbuddy](https://osmbuddy.apps.grok.bot). Public releases are [EmanuelKedmi/osmbuddy-companion](https://github.com/EmanuelKedmi/osmbuddy-companion). GitHub serves `MarketNotes.Companion.exe` from the latest release. The website only redirects there, and the file is not hosted on OCI.

A `feat` or `fix` commit on `main` creates the next `vMAJOR.MINOR.PATCH` tag. GitHub Actions stamps that version onto `MarketNotes.Companion.exe` and attaches it to the release.

A Windows app you run beside Old School Maple. It owns the screen share, the FM scanner, the training scanner, and the stat and skill window scans. The website can be closed. `/market/fm`, `/farm`, and the Characters page are optional viewers of a copy the companion may send after the local result is already on screen.

This is the same idea as the newer MapleStory Classic world-market tools that read a store by looking at the window. It does not read process memory, sniff packets, inject code, or bypass anything.

Live OCR is not implemented. `OcrStoreScanner.EngineInstalled()` stays false. **Load FM sample** and **Send training sample** run the C# parsers on saved fixtures. They are labeled samples, not a reading of the game.

## Screen share

1. Choose **Window** or **Entire screen**.
2. Pick a window (title and process) or a screen (index and bounds). The chosen source is described before sharing starts. If a still frame can be grabbed, it shows in the preview.
3. **Start share**. The preview updates about twice a second and names what is being shared.
4. **Stop share**. That stops the capture and both scanners.

The capture loop uses `IScreenCapture`. This build runs `PrintWindowScreenCapture`: `PrintWindow` for a window (`PW_RENDERFULLCONTENT`), then `CopyFromScreen` of that window’s rectangle if the frame is blank. An entire screen is `CopyFromScreen` of the monitor bounds from `EnumDisplayMonitors`.

Windows Graphics Capture (`Windows.Graphics.Capture`, the API Discord and Teams use) is the Windows upgrade. This SDK does not include that WinRT projection, so the project does not reference it and does not report a Graphics Capture frame. `WindowsGraphicsCapture.IsAvailable` is false. See `Capture/WindowsGraphicsCapture.cs`.

The live preview is the surface for overlays. FM hover prices sit on the shop rows in that preview. Training kills, time, and loot rates sit on the preview and in the training panel.

## In-game notes

Ctrl+Shift+M, or the Notes button, opens a small panel on the game window. The same key, or the panel's Close button, puts the panel away. The Notes button says Active while any section is selected, and it keeps saying that after the panel is closed. The panel is a window parked on the client. It is not drawn into the game, and it does not read process memory.

While a section is selected, the companion looks for the client's item tooltip near the pointer, whether or not the panel is open. It finds the tooltip by the solid pale square behind the item icon, which looks the same over any background, inside the tooltip's translucent navy. The item name is the tooltip's title line. The card stays closed until that name has settled, then opens beside the tooltip, on the right when it fits and on the left when the right edge would leave the client. Listing and sale prices can fill in after the card is already open.

The card is a stack of sections. Price trend shows the database price, the median listing and the median sale for the hovered item from the website, and the low, the latest, and the high from shops this companion has read. The middle half of the prices is the bulk. A price in the top or bottom quarter is left out when it is at least 5× away from that bulk, so a 20,000,000 or 500,000,000 tag counts only when most prices are near it. Active shops says how many shops still have it, and the card expands to the owner, the place, and the price. Gear rolls lists the min, the average, and the max of each stat on a dropped equip. The base stat is the average. A positive stat rolls evenly down and up by the smaller of one tenth of that base, rounded up, and 5, or 10 for defense, HP, and MP. A scroll or a use item has no rolls. The panel's checkboxes turn sections on and off.

Shop rows the FM scanner reads, and sales it marks, are the prices stored for that trend. They are written to `%LocalAppData%/MarketNotes.Companion/hover-market.json`. Sample rows are not stored. A shop that leaves the screen is no longer active. Its earlier prices stay on the trend.

## Modes

There is no FM / training switch. Every frame, the companion reads the map name from the minimap title. A Free Market map runs the FM scanner. Any other map, including a blank or unknown name, runs the Training scanner. Changing maps stops the other scanner immediately. The choice is not saved.

When the minimap title cannot be read, the map name box is the map. A Free Market name shows the FM scanner, and any other name shows the Training scanner. **Load FM sample** and **Send training sample** follow that map.

### FM scanner

A frame is a Free Market frame only when the map name is one of the GMS v62 Free Market rooms in `data/fm-maps.json` or an alias already used here: "Free Market", "FM 1", "FM Channel 1", "Free Market Entrance". A hunting map, including Mushroom Garden, is skipped with the reason "not an FM map". An unknown map name is not treated as FM. The room number in `Free Market<1>` is not a channel.

The shop state is the owner, shop name, map name, channel when the frame actually has one, and the scrollbar slice (position 0–1, plus the first visible row and how many rows are on screen). The same owner, shop, map, channel, and scrollbar are not scanned again. A scrollbar move adds only the newly revealed rows. An owner or shop name change resets the shop. A row is kept once per slot identity: item name, visible row, and scrollbar slice.

**Load FM sample** steps `data/fixtures/fm-shop.json` through that state machine inside the companion. The rows are marked sample. OCR did not read them. Hover a row on the preview for the shop price, the database price, the source label, and the gap. Prices are loaded from `data/osm-market-prices.json` and the classic and NPC prices in `data/catalog.json`. The order here is Discord sold, then Discord asks, then classic, then NPC. There is no typed override in the companion. The labels are "Discord market · sold", "Discord market · asks", "Classic", "NPC", and "Not in the database".

While a share is on and OCR is off, the loop reports crop sizes. It does not invent shop rows.

### Training scanner

The same share is cropped into the battle analysis box and the lower-left loot corner. Each region is hashed. An unchanged region is skipped. With OCR off, a changed region is a heartbeat only. It does not invent kills or items, and it does not build FM shop state.

**Send training sample** runs the C# parser on `data/fixtures/training-sample.json` with `ocr` false. The battle snippet supplies kills, time, and any exp, damage, and attacks written in that snippet. Those kills and minutes are the session counters. The loot lines are counted once. A repeated region does not count again. Damage in the snippet is what the window showed. It does not replace the character-sheet formula.

Rates are quantity per kill and per hour for the character name typed in the companion. Observations are stored in `%LocalAppData%/MarketNotes.Companion/loot-pool.json`, not in the git repo. Library chances are not overwritten. A small bundled table (`data/fixtures/library-chances.json`) supplies the catalog chance for the sample map. Expected loot stays on that library rate until the pool has 100 kills on the map and has seen the item, and it matches the pool at 1,000 kills. If a chance is missing, the observed per-kill rate is shown and the report says the library blend needs the catalog chance.

Fallback kills and minutes are used only when a battle window was read and it had no kill count or time. A live share with OCR off does not apply those boxes. A heartbeat does not change the session's kill count.

### Character scanner

This is not a third mode. It runs on the same share while either scanner is selected. It looks for the client's framed windows. Windows OCR, the same reader the battle window uses, has to return the text. With OCR off, a framed window is a heartbeat only. It does not invent stats or skill levels. A window that was read and is not a stat or skill window adds nothing.

The stat window match key is the character name. The same name, ignoring case, updates that character. A different name creates one. If the name cannot be read from the stat window or from the status plate, nothing is created and nothing else is updated. Fields written are the ones the window showed: job, level, STR, DEX, INT, LUK, max HP, weapon attack, magic attack, accuracy, weapon def, and magic def. Unread fields are left alone on an update. A new character keeps the blank sheet for anything the window did not show. MP is read and not stored, because the character sheet has no MP field. Accuracy on a magician is not written over magic accuracy. Weapon attack and magic attack are the numbers on the window. A skill scan still adds Rage or Meditation on top of those fields.

The skill window does not print the character name. **Load skill sample** uses the name in `data/fixtures/skill-window.json`. A live read uses the status plate, and only when that name was actually read. No matching character means the skills are not saved and a character is not created. Levels written are the ones the planner already uses: the saved attack, or the highest ranked attack on the window when the saved attack is not there, plus mastery, booster, Rage, Critical Shot, Critical Throw, Focus, Nimble Body, Shadow Partner, Meso Up, and Meditation when that buff belongs to the job. Unknown skills and skills with no readable level are skipped. They are not set to 0.

**Load stat sample** runs the parser on `data/fixtures/stat-window.json`. **Load skill sample** runs the parser on `data/fixtures/skill-window.json`. Both are marked sample (`ocr` false). When the website mirror address is the farm feed, a copy is posted to the same host at `/api/character/feed`. The open site writes it into `osm-field-notes-v1` in the browser. `data/character-scan.json` is only that handoff. It is not an account and it is not the character save.

## What a shop scan writes

The FM scanner keeps the shop in `FmScanMachine`. **Export JSON** writes that shop as `market-notes-scan.json` through a save dialog. The scanned time is the time of the export. Owner and location come from the shop state (map name, and channel when the frame had one).

`IStoreScanner` is the older OCR seam. `OcrStoreScanner.EngineInstalled()` returns false. `SampleStoreScanner` still returns obvious `[Sample]` lines if something calls it. The screen-share loop does not. It does not invent rows from pixels.

## Tests

```bash
dotnet test companion/MarketNotes.Companion.sln
dotnet build companion/MarketNotes.Companion.sln -c Release
```

The scanner decisions live in `MarketNotes.Companion.Core` (`net8.0`) so `dotnet test` runs on Linux. The WPF project references that library.

## Build on Windows

You need the .NET 8 SDK on Windows (the desktop pack that ships with it). From the repository root:

```bash
dotnet test companion/MarketNotes.Companion.sln
dotnet build companion/MarketNotes.Companion.sln -c Release
dotnet run --project companion/MarketNotes.Companion/MarketNotes.Companion.csproj
```

The project is `net8.0-windows` with `UseWPF`. `EnableWindowsTargeting` is set so a non-Windows SDK can compile the same project. `dotnet build companion/MarketNotes.Companion.sln -c Release` succeeds with the .NET 8 SDK. The output is a Windows binary. This checkout did not launch it, and it did not scan a game window.

Release output:

```text
companion/MarketNotes.Companion/bin/Release/net8.0-windows/MarketNotes.Companion.exe
```

## JSON contract

Property names are the schema. They match `lib/market.ts`. The companion writes a **store scan**. The website’s Export button writes a **market book**. Import on the site accepts either, and also a single shop object or an array of shops. Import only merges shops: the same owner and place (trimmed, case-insensitive) replaces that shop’s lines and `scannedAt`. The handwritten note, id, and created time stay. Sale offers, wanted notes, and auctions are left alone.

Prices are whole mesos. `slot` is a number, or `null` when the slot is unknown. Times are ISO-8601.

### Store scan (what this app writes)

```ts
export interface StoreLine {
  item: string;
  quantity: number;
  /** Meso price for a single item. */
  priceEach: number;
  /** Shop slot when the player or a scanner knows it. Null when unknown. */
  slot: number | null;
}

/** One shop inside a companion export. No id. The site matches owner + location. */
export interface CompanionStore {
  owner: string;
  location: string;
  scannedAt: string;
  lines: StoreLine[];
}

/** What the Windows companion writes, and what Import merges. */
export interface CompanionScanExport {
  version: 1;
  kind: "store-scan";
  stores: CompanionStore[];
}
```

```json
{
  "version": 1,
  "kind": "store-scan",
  "stores": [
    {
      "owner": "[Sample] Mushmom Shop",
      "location": "[Sample] FM Channel 1",
      "scannedAt": "2026-11-02T15:04:00.000Z",
      "lines": [
        { "item": "[Sample] Scroll for Gloves for DEX 10%", "quantity": 1, "priceEach": 350000, "slot": 1 }
      ]
    }
  ]
}
```

`companion/sample-scan.json` is a full sample of that file. Every item name in it starts with `[Sample]`.

### Market book (what the website exports)

The book lives in the browser under `osm-market-notes-v1`. It is not the character save (`osm-field-notes-v1`).

```ts
export interface SaleListing {
  id: string;
  item: string;
  quantity: number;
  /** Asking price in mesos for the whole quantity. */
  price: number;
  seller: string;
  note: string;
  /** ISO-8601. */
  createdAt: string;
}

export interface BuyListing {
  id: string;
  item: string;
  quantity: number;
  /** Maximum mesos the buyer will pay for the whole quantity. */
  maxPrice: number;
  buyer: string;
  note: string;
  createdAt: string;
}

export interface AuctionListing {
  id: string;
  item: string;
  quantity: number;
  startingBid: number;
  /** Null when nobody has bid. */
  currentBid: number | null;
  seller: string;
  /** ISO-8601. The auction is open while this instant is still ahead. */
  endsAt: string;
  note: string;
  createdAt: string;
}

export interface StoreListing {
  id: string;
  owner: string;
  /** Free text, for example "FM 1 · Room 4". */
  location: string;
  /** ISO-8601 of the last scan, or the last time the lines were written by hand. */
  scannedAt: string;
  lines: StoreLine[];
  note: string;
  createdAt: string;
}

export interface MarketBook {
  version: 1;
  sales: SaleListing[];
  buys: BuyListing[];
  auctions: AuctionListing[];
  stores: StoreListing[];
}
```

A shop inside the book is a `StoreListing`: the scan fields, plus `id`, `note`, and `createdAt`. A line is the same `StoreLine` as in a scan.

## Not implemented yet

- Live OCR for shop rows and kill counts. No Tesseract package. `OcrStoreScanner.EngineInstalled()` follows Windows OCR, and a changed region is a heartbeat when that reader returns no text. It is not a guess. The stat and skill window use the same rule: an unreadable frame does not create a character or a skill level. **Load stat sample** and **Load skill sample** are the fixture path.
- Windows Graphics Capture. The interface is `IScreenCapture`. The class that runs is PrintWindow / CopyFromScreen. `WindowsGraphicsCapture` does not grab pixels in this build.
- Any memory read, packet capture, injection, or client modification.

## Where the results live

Shop state, prices, kills, and loot rates are computed in the companion (`MarketNotes.Companion.Core`). A copy can be posted to the website after that, if the optional mirror URL is set and the site is open. A closed website does not clear the companion. The companion's loot pool is `%LocalAppData%/MarketNotes.Companion/loot-pool.json`. It does not write `data/catalog.json`. The website's `data/farm-observations.json` and `data/fm-scan.json` are only the optional viewer ledger.
