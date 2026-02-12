using System;
using System.Windows;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RedeliAvad
{
    [Transaction(TransactionMode.Manual)]
    public class Command : IExternalCommand
    {
        // 🔒 Static reference to enforce a single window instance
        private static MainWindow _mainWindowInstance;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // ✅ If the window already exists
                if (_mainWindowInstance != null)
                {
                    if (_mainWindowInstance.WindowState == WindowState.Minimized)
                        _mainWindowInstance.WindowState = WindowState.Normal;

                    _mainWindowInstance.Activate(); // Bring to front
                    return Result.Succeeded;
                }

                // Access the current Revit document and active view
                UIDocument uiDoc = commandData.Application.ActiveUIDocument;
                Document doc = uiDoc.Document;
                View currentView = doc.ActiveView;

                // ✅ Create and store the instance
                _mainWindowInstance = new MainWindow(uiDoc, doc, currentView);
                _mainWindowInstance.WindowStartupLocation = WindowStartupLocation.Manual;

                // ✅ Release reference when window is closed
                _mainWindowInstance.Closed += (s, e) => _mainWindowInstance = null;

                // ✅ Set Revit as the owner for correct window focus
                System.Windows.Interop.WindowInteropHelper helper = new System.Windows.Interop.WindowInteropHelper(_mainWindowInstance)
                {
                    Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle
                };

                _mainWindowInstance.Show(); // Use Show() for modeless display
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
