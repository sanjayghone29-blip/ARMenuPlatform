using Microsoft.AspNetCore.Mvc;

namespace ARMenuPlatform.Controllers
{
    // =========================================================================
    // ARViewerController
    // Serves the public-facing customer AR experience pages.
    // No authentication required — these are scanned by restaurant customers.
    // =========================================================================
    public class ARViewerController : Controller
    {
        // =====================================================================
        // GET /ar/menu/{qrCodeString}
        // Returns the AR menu Razor page.
        // The view itself fetches data via JS → /api/menu/{qrCodeString}
        // =====================================================================
        [HttpGet]
        [Route("ar/menu/{qrCodeString}")]
        public IActionResult Menu(string qrCodeString)
        {
            // Basic sanity check before rendering the page
            if (string.IsNullOrWhiteSpace(qrCodeString) ||
                !Guid.TryParse(qrCodeString, out _))
            {
                return BadRequest("Invalid QR code.");
            }

            // Pass the GUID to the view — all data fetching is done client-side
            ViewBag.QrCodeString = qrCodeString;
            return View();
        }

        // =====================================================================
        // GET /ar/video
        // Returns the Video AR experience page.
        // Dish details are passed as query string parameters by Menu.cshtml.
        //
        // Query params:
        //   dishId       — int
        //   dishName     — string
        //   price        — decimal
        //   contentURL   — string (Azure Blob MP4 URL)
        //   qrCodeString — GUID (used to derive the .mind marker file)
        //   qrImageUrl   — string (Azure Blob PNG URL of the QR code image)
        // =====================================================================
        [HttpGet]
        [Route("ar/video")]
        public IActionResult VideoAR()
        {
            // All params are read by JavaScript from window.location.search
            // Nothing to do server-side — just return the view
            return View();
        }
    }
}