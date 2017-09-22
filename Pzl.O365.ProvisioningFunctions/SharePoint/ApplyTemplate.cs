using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Formatting;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Http.Description;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.SharePoint.Client;
using OfficeDevPnP.Core.Framework.Provisioning.Connectors;
using OfficeDevPnP.Core.Framework.Provisioning.Model;
using OfficeDevPnP.Core.Framework.Provisioning.ObjectHandlers;
using OfficeDevPnP.Core.Framework.Provisioning.Providers;
using OfficeDevPnP.Core.Framework.Provisioning.Providers.Xml;
using Pzl.O365.ProvisioningFunctions.Helpers;

namespace Pzl.O365.ProvisioningFunctions.SharePoint
{
    public static class ApplyTemplate
    {
        static ApplyTemplate()
        {
            //RedirectAssembly();
        }

        [FunctionName("ApplyTemplate")]
        [ResponseType(typeof(ApplyTemplateResponse))]
        [Display(Name = "Apply PnP template to site", Description = "Apply a PnP template to the site.")]
        public static async Task<HttpResponseMessage> Run([HttpTrigger(AuthorizationLevel.Function, "post")]ApplyTemplateRequest request, TraceWriter log)
        {
            string siteUrl = request.SiteURL;
            RedirectAssembly();
            try
            {
                var clientContext = await ConnectADAL.GetClientContext(siteUrl, log);

                Uri fileUri = new Uri(request.TemplateURL);
                var webUrl = Web.WebUrlFromFolderUrlDirect(clientContext, fileUri);
                var templateContext = clientContext.Clone(webUrl.ToString());

                var library = request.TemplateURL.ToLower().Replace(templateContext.Url.ToLower(), "").TrimStart('/');
                var idx = library.IndexOf("/", StringComparison.Ordinal);
                library = library.Substring(0, idx);

                // This syntax creates a SharePoint connector regardless we have the -InputInstance argument or not
                var fileConnector = new SharePointConnector(templateContext, templateContext.Url, library);
                string templateFileName = Path.GetFileName(request.TemplateURL);
                XMLTemplateProvider provider = new XMLOpenXMLTemplateProvider(new OpenXMLConnector(templateFileName, fileConnector));
                templateFileName = templateFileName.Substring(0, templateFileName.LastIndexOf(".", StringComparison.Ordinal)) + ".xml";
                var provisioningTemplate = provider.GetTemplate(templateFileName, new ITemplateProviderExtension[0]);

                foreach (var parameter in request.Parameters)
                {
                    provisioningTemplate.Parameters[parameter.Key] = parameter.Value;
                }

                ResolveListWebParts(provisioningTemplate, clientContext.Web);

                provisioningTemplate.Connector = provider.Connector;

                ProvisioningTemplateApplyingInformation applyingInformation = new ProvisioningTemplateApplyingInformation()
                {
                    ProgressDelegate = (message, progress, total) =>
                    {
                        log.Info(String.Format("{0:00}/{1:00} - {2}", progress, total, message));
                    },
                    MessagesDelegate = (message, messageType) =>
                    {
                        log.Info(String.Format("{0} - {1}", messageType, message));
                    }
                };


                clientContext.Web.ApplyProvisioningTemplate(provisioningTemplate, applyingInformation);


                return await Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ObjectContent<ApplyTemplateResponse>(new ApplyTemplateResponse { TemplateApplied = true }, new JsonMediaTypeFormatter())
                });
            }
            catch (Exception e)
            {
                log.Error($"Error: {e.Message}\n\n{e.StackTrace}");
                return await Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new ObjectContent<string>(e.Message, new JsonMediaTypeFormatter())
                });
            }
        }

        private static void ResolveListWebParts(ProvisioningTemplate provisioningTemplate, Web web)
        {
            // List patch until PnP is updated
            if (provisioningTemplate.ClientSidePages == null) return;
            var tokenParser = new TokenParser(web, provisioningTemplate);
            foreach (var page in provisioningTemplate.ClientSidePages)
            {
                foreach (var section in page.Sections)
                {
                    foreach (var control in section.Controls)
                    {
                        control.JsonControlData = tokenParser.ParseString(control.JsonControlData);
                        if (control.Type != WebPartType.List) continue;
                        ListResolver resolver = new ListResolver(control, web);
                        resolver.Process();
                    }
                }
            }
        }

        public static void RedirectAssembly()
        {
            var list = AppDomain.CurrentDomain.GetAssemblies().OrderByDescending(a => a.FullName).Select(a => a.FullName).ToList();
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                var requestedAssembly = new AssemblyName(args.Name);
                foreach (string asmName in list)
                {
                    if (asmName.StartsWith(requestedAssembly.Name + ","))
                    {
                        return Assembly.Load(asmName);
                    }
                }
                return null;
            };
        }

        public class ApplyTemplateRequest
        {
            [Required]
            [Display(Description = "URL of site to apply template")]
            public string SiteURL { get; set; }

            [Required]
            [Display(Description = "SPO URL to the .pnp template")]
            public string TemplateURL { get; set; }

            [Display(Description = "Replacement tokens to be used in .pnp templates")]
            public Parameter[] Parameters { get; set; }
        }

        public class Parameter
        {
            [Required]
            [Display(Description = "Extra PnP token to parse for template. Example. 'Foo' becomes '{parameter:Foo}' in the template.")]
            public string Key { get; set; }

            [Required]
            [Display(Description = "Value to replace with")]
            public string Value { get; set; }
        }


        public class ApplyTemplateResponse
        {
            [Display(Description = "True if template was applied")]
            public bool TemplateApplied { get; set; }
        }
    }
}
