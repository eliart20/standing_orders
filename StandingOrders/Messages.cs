using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PX.Data;
using PX.Common;


namespace StandingOrders
{
     [PXLocalizable]
    public static class STMessages           // <- localizable constants
    {
        // Order and Shipment Messages
        public const string OrderNotFound = "Sales Order {0}-{1} was not found.";
        public const string NoOpenQtyForItem = "Order {0}-{1} has no open quantity for item {2}.";
        public const string SiteCannotBeBlank = "Site cannot be determined for shipment.";
        
        // Series Processing Messages
        public const string SeriesNotFoundForBookSeries = "Series not found for the selected Book Series.";
        public const string SelectBookSeriesFirst = "Select a Book Series first.";
        public const string NoShippableLinesFound = "No shippable lines found for the current filter.";
        public const string NoSeriesDetailRowsMatch = "No SeriesDetail rows match the items being shipped.";
        
        // UI Display Names
        public const string ProcessSeries = "Process Series";
        public const string BookSeries = "Book Series";
        public const string InventoryID = "Inventory ID";
        
        // Dialog Titles
        public const string ConfirmUpdates = "Confirm Updates";

        // Validation Messages
        public const string MultipleCyclesDetected = "Cannot process shipment: multiple cycles detected. Only one cycle can be shipped at a time.\n\n{0}";

    }
}
