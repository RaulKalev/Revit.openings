# Revit.openings (RedeliAvad)

A Revit plugin that automatically detects intersections between cable trays and structural elements (walls, floors, etc.) and places opening family instances at the collision points.

## Features

- **Automatic intersection detection** — Scans the active view for cable trays crossing walls, floors, and other host elements
- **One-click opening placement** — Places correctly sized and oriented opening families at each detected intersection
- **Duplicate prevention** — Skips locations where an opening instance already exists
- **Level-aware placement** — Automatically associates placed openings with the nearest level
- **Rotation matching** — Orients openings to align with the cable tray direction
- **Material Design UI** — Modern WPF interface with dark theme support, custom title bar, and draggable/resizable window

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
│   ├── TitleBar.xaml/.cs    # Custom window chrome
│   ├── Themes/              # MaterialDesign theme resources
│   └── WindowResizer.cs     # Drag & resize logic
├── PlaceIntersectionsHandler.cs   # Core intersection & placement logic
├── PlacementHelpers.cs            # Geometry helper utilities
├── FocusNavigateHandler.cs        # View navigation helpers
├── TempMemory.cs                  # Runtime state cache
└── Resources/
    └── RedeliAvad.tiff            # Ribbon button icon
```

## License

See [LICENSE](LICENSE) for details.
