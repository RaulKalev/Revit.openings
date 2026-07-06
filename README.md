# Revit.openings (RedeliAvad)

A Revit plugin that automatically detects intersections between cable trays and structural elements (walls, floors, etc.) and places opening family instances at the collision points.

## Features

- **Automatic intersection detection** — Scans the active view for cable trays crossing walls, floors, and other host elements
- **One-click opening placement** — Places correctly sized and oriented opening families at each detected intersection
- **Duplicate prevention** — Skips locations where an opening instance already exists
- **Level-aware placement** — Automatically associates placed openings with the nearest level
- **Rotation matching** — Orients openings to align with the cable tray direction
- **Opening Manager** — Persistent opening↔source↔host links, validation, navigation, automatic fixing and allowed-exception workflow (see below)
- **Material Design UI** — Modern WPF interface with dark theme support, custom title bar, and draggable/resizable window

## Opening Manager

Open it from the main window (**Avade haldur** button). It lists every opening created by this plugin, with search, status filter chips (Kõik / Vertikaalne / Horisontaalne / Joondatud / Joondamata / Vajab ülevaatust / Lubatud / Puudub), a schematic alignment preview and per-row actions.

### How links are stored

When an opening is placed, the plugin writes an **Extensible Storage** record onto the opening instance (schema `RKTools.Openings.OpeningLink`, stable GUID, vendor `RKTL`, versioned for future migrations). The record stores the opening, source (cable tray) and host (wall/framing) as **UniqueIds** (ElementIds only as convenience), linked-model information (RevitLinkInstance UniqueId + element UniqueId inside the link), sizes/clearances in mm, and geometry signatures (hashes of location/bounding box/direction/size). A project-level `DataStorage` index (`RKTools.Openings.Index`) mirrors one compact entry per opening so that *deleted* openings remain detectable and recreatable. All data travels with the model — links survive closing/reopening Revit and worksharing.

### Validating

Press **Valideeri**. The scan only touches plugin-managed openings (quick storage filter) and compares stored geometry signatures against the current model. Detected states: Joondatud (Aligned), Allikas liikunud (SourceMoved), Ava liikunud (OpeningMoved), Alus muutunud (HostChanged), Joondamata (NotAligned), Vale suurus (SizeMismatch), Ava/Allikas/Alus puudub (Missing*), Lubatud (Allowed), Vajab ülevaatust / Viga. Alignment tolerance is 5 mm. While the window is open, model changes mark affected rows as "muudetud" — heavy scans only run when you press Valideeri.

### Fixing openings

Rows with the wrench icon enabled can be fixed: the plugin recomputes the source↔host penetration point, moves the opening there and resizes it to source size + stored clearance. A confirmation always shows what will change (e.g. "Nihuta ava 143 mm … uuenda suurus 200×100 → 250×150 mm?"). Deleted openings can be recreated at the current intersection after confirmation. **Paranda valitud** fixes all selected rows after a summary confirmation.

### "Allowed" (Lubatud)

If a deviation is intentional (opening deliberately offset, oversized, or handled manually), mark the row **Lubatud** with an optional reason. The reason, user and timestamp persist in storage; the row stops showing as an error but stays visible under the *Lubatud* filter. The shield-off icon clears the exception. Missing elements are never masked by Allowed.

### Known limitations

- Sources are expected in the current model and hosts in the selected linked model (the current placement workflow); records support both in links, but selection of linked elements is not possible in Revit — the manager zooms to their transformed bounding box instead.
- If a linked model is unloaded, its elements report *Alus/Allikas puudub* ("link pole laaditud") until reloaded.
- Alignment math uses solid intersections with bounding-box fallbacks; exotic geometry may fall back to "Vajab ülevaatust" rather than an exact expected point.
- Round/sleeve opening shapes are stored but the bundled workflow currently places rectangular openings.

Diagnostics log: `%LocalAppData%\RK Tools\Revit.openings\opening-manager.log`.

## Tech Stack

| Component | Details |
|---|---|
| Platform | Autodesk Revit 2024+ |
| Framework | .NET Framework 4.8 / WPF |
| UI | MaterialDesignThemes 5.2, custom title bar |
| Packaging | Costura.Fody (single-DLL deployment) |
| Revit helpers | ricaun.Revit.UI |
| Serialization | Newtonsoft.Json |

## Prerequisites

- Autodesk Revit 2024 or later
- Visual Studio 2022
- .NET Framework 4.8 SDK

## Getting Started

```bash
git clone https://github.com/RaulKalev/Revit.openings.git
```

1. Open `RedeliAvad.sln` in Visual Studio.
2. Update the Revit API assembly references in `RedeliAvad.csproj` to match your local Revit installation path.
3. Restore NuGet packages.
4. Build the solution — output DLL will be produced by Costura.Fody as a single merged assembly.
5. Load the plugin into Revit (copy to the Revit Addins folder or use a `.addin` manifest).

## Usage

1. In Revit, go to the **RK Tools** tab → **Tools** panel → click **Avade loomine**.
2. The plugin window opens. Configure your opening family and parameters.
3. Click to detect intersections and place openings in the active view.

## Project Structure

```
RedeliAvad/
├── Setup/
│   ├── App.cs              # IExternalApplication — ribbon tab & button
│   └── Command.cs           # IExternalCommand — launches the UI
├── UI/
│   ├── MainWindow.xaml/.cs  # Primary plugin window
│   ├── OpeningManagerWindow.xaml/.cs  # Opening Manager (list, filters, actions)
│   ├── AllowReasonDialog.xaml/.cs     # "Mark as allowed" reason prompt
│   ├── TitleBar.xaml/.cs    # Custom window chrome
│   ├── Themes/              # MaterialDesign theme resources
│   └── WindowResizer.cs     # Drag & resize logic
├── OpeningManager/
│   ├── OpeningLinkStorage.cs        # Extensible Storage schemas + index (stable GUIDs)
│   ├── OpeningLinkRecord.cs         # Stored record + index entry models
│   ├── OpeningValidationService.cs  # Validation engine (statuses, expected geometry)
│   ├── OpeningGeometry.cs           # Geometry utilities + signatures
│   ├── OpeningManagerHandler.cs     # ExternalEvent handler (navigate/fix/allow/…)
│   ├── OpeningManagerItem.cs        # UI row model
│   ├── OpeningStatus.cs             # Status enum + severity/colours
│   └── OpeningManagerLog.cs         # File logger
├── PlaceIntersectionsHandler.cs   # Core intersection & placement logic
├── PlacementHelpers.cs            # Geometry helper utilities
├── FocusNavigateHandler.cs        # View navigation helpers
├── TempMemory.cs                  # Runtime state cache
└── Resources/
    └── RedeliAvad.tiff            # Ribbon button icon
```

## License

See [LICENSE](LICENSE) for details.
