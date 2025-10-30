using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WeddingShare.Views.Home
{
    public class IndexModel : PageModel
    {
        public IndexModel() 
        {
            this.GalleryNames = new Dictionary<string, string>() { { "default", "default" } };
        }

        public IDictionary<string, string> GalleryNames { get; set; }

        public void OnGet()
        {
        }
    }
}