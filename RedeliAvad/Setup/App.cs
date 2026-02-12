using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ricaun.Revit.UI;

namespace RedeliAvad
{
    [AppLoader]
    public class App : IExternalApplication
    {
        private RibbonPanel ribbonPanel;

        public Result OnStartup(UIControlledApplication application)
        {
            const string tabName = "RK Tools";
            const string panelName = "Tools";

            try { application.CreateRibbonTab(tabName); } catch { }
            ribbonPanel = application.CreateOrSelectPanel(tabName, panelName);

            // Create the buttons
            ribbonPanel.CreatePushButton<Command>("Avade\nloomine")
                .SetLargeImage("Resources/RedeliAvad.tiff")
                .SetToolTip("Avade loomine redelite ja mudeli ristumiste kohtades.")
                .SetContextualHelp("https://raulkalev.github.io/rktools/");


            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            ribbonPanel?.Remove();
            return Result.Succeeded;
        }
    }
}
