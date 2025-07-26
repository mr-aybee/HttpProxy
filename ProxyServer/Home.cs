using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace ProxyServerApp
{
	public partial class Home : Form
	{
		private ProxyServer proxyServer;
		private string[] blockedWebs = [];
		public static string HomeDirectory { get; private set; } = Environment.CurrentDirectory;

		public Home()
		{
			InitializeComponent();

			// Bypass certificate check for all requests
			ServicePointManager.ServerCertificateValidationCallback +=
				(s, certificate, chain, sslPolicyErrors) => true;

			HomeDirectory = Environment.CurrentDirectory;
			decryptSSLCheck.Checked = Properties.Settings.Default.isDecryptSSL;
			portNum.Value = Properties.Settings.Default.Port;

			// Load blocked websites from file at startup
			LoadBlockedWebs();

			if (Properties.Settings.Default.isStartOnLoad)
			{
				StartProxy();
			}

			FormClosed += (x, y) =>
			{
				Application.Exit();
				Environment.Exit(0);
			};
		}

		private void StartBtn_Click(object sender, EventArgs e)
		{
			StartProxy();
		}

		/// <summary>
		/// Starts the proxy server with configured endpoints.
		/// </summary>
		private void StartProxy()
		{
			if (portNum.Value == 0)
			{
				MessageBox.Show("Please Enter available Port Number");
				return;
			}

			try
			{
				proxyServer = new ProxyServer();

				// Event handlers for proxy server
				proxyServer.BeforeRequest += OnRequest;
				proxyServer.BeforeResponse += OnResponse;
				proxyServer.ServerCertificateValidationCallback += OnCertificateValidation;
				proxyServer.ClientCertificateSelectionCallback += OnCertificateSelection;

				// Explicit endpoint (client knows about proxy)
				var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, (int)portNum.Value, decryptSSLCheck.Checked);
				explicitEndPoint.BeforeTunnelConnectRequest += OnBeforeTunnelConnectRequest;
				proxyServer.AddEndPoint(explicitEndPoint);

				// Transparent endpoint (client does not know about proxy)
				var transparentEndPoint = new TransparentProxyEndPoint(IPAddress.Any, ((int)portNum.Value + 1), true);
				proxyServer.AddEndPoint(transparentEndPoint);

				proxyServer.Start();

				portNum.Enabled = false;
				startBtn.Enabled = false;

				foreach (var endPoint in proxyServer.ProxyEndPoints)
				{
					var logMsg = $"Listening on '{endPoint.GetType().Name}' endpoint at Ip {endPoint.IpAddress} and port: {endPoint.Port}\n";
					majorLogsTxt.AppendText(logMsg);
					Logger.Instance.Log(logMsg);
				}
			}
			catch (Exception ex)
			{
				Logger.Instance.LogError(ex.Message, ex);
				majorLogsTxt.AppendText(ex.Message);
			}
		}

		/// <summary>
		/// Handles incoming requests, blocks if URL matches blocked list, and logs request.
		/// </summary>
		public async Task OnRequest(object sender, SessionEventArgs e)
		{
			var url = e.HttpClient.Request.Url;
			if (IsUrlBlocked(url))
			{
				e.Ok("<!DOCTYPE html><html><body><h1>Website Blocked</h1><p>Blocked by XC.</p></body></html>");
				return;
			}

			// Log request asynchronously
			await Task.Run(() =>
			{
				var datetime = DateTime.Now;
				Logger.Instance.Log($"""
                    -----------
                    {datetime:yyyyMMdd_HHmmss.fff}
                    {url}
                    """);

				if (detailedLogsCheck.Checked)
				{
					Logger.Instance.LogVerbose($"""
                        -----------
                        {datetime:yyyyMMdd_HHmmss.fff}

                        Request Header
                        {string.Join("\r\n", e.HttpClient.Request.Headers.GetAllHeaders().Select(x => $"{x.Name}:{x.Value}"))}

                        UserData
                        {e.HttpClient.UserData}

                        TimeLine
                        {string.Join("\r\n", e.TimeLine.Select(x => $"{x.Key}:{x.Value}"))}
                        [][][]
                        """);
				}

				// Update UI thread safely
				Invoke(() => Text = "[Connects]" + proxyServer.ThreadPoolWorkerThread);
			}).ConfigureAwait(false);
		}

		private void StopBtn_Click(object sender, EventArgs e)
		{
			Application.Exit();
		}

		/// <summary>
		/// Opens the blocked websites file for editing and reloads the list.
		/// </summary>
		private void BlockFileBtn_Click(object sender, EventArgs e)
		{
			var blockFilePath = Path.Combine(HomeDirectory, "BlockedWebs.txt");
			using (var process = Process.Start("notepad.exe", blockFilePath))
			{
				process?.WaitForExit();
			}
			MessageBox.Show("Blocks Updated");
			LoadBlockedWebs();
		}

		private void ClearMajorLogsaBtn_Click(object sender, EventArgs e)
		{
			majorLogsTxt.Clear();
		}

		private async Task OnCertificateSelection(object sender, CertificateSelectionEventArgs e)
		{
			await Task.CompletedTask;
		}

		private async Task OnCertificateValidation(object sender, CertificateValidationEventArgs e)
		{
			await Task.CompletedTask;
		}

		private async Task OnResponse(object sender, SessionEventArgs e)
		{
			await Task.CompletedTask;
		}

		/// <summary>
		/// Handles HTTPS tunnel connect requests, blocks if URL matches blocked list.
		/// </summary>
		private async Task OnBeforeTunnelConnectRequest(object sender, TunnelConnectSessionEventArgs e)
		{
			var url = e.HttpClient.Request.Url;
			if (IsUrlBlocked(url))
			{
				e.DecryptSsl = true;
			}
			await Task.CompletedTask;
		}

		/// <summary>
		/// Loads blocked websites from file into memory.
		/// </summary>
		private void LoadBlockedWebs()
		{
			var blockFilePath = Path.Combine(HomeDirectory, "BlockedWebs.txt");
			if (File.Exists(blockFilePath))
			{
				blockedWebs = [.. File.ReadAllLines(blockFilePath)
					.Where(line => !string.IsNullOrWhiteSpace(line))
					.Select(line => line.Trim())];
			}
			else
			{
				blockedWebs = Array.Empty<string>();
			}
		}

		/// <summary>
		/// Checks if the given URL matches any blocked website prefix.
		/// </summary>
		private bool IsUrlBlocked(string url)
		{
			// Use StringComparison.OrdinalIgnoreCase for case-insensitive matching
			return blockedWebs.Any(blocked =>
				!string.IsNullOrEmpty(blocked) &&
				url.StartsWith(blocked, StringComparison.OrdinalIgnoreCase));
		}
	}
}
