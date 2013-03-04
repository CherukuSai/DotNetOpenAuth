﻿//-----------------------------------------------------------------------
// <copyright file="WebServerClient.cs" company="Outercurve Foundation">
//     Copyright (c) Outercurve Foundation. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace DotNetOpenAuth.OAuth2 {
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.Linq;
	using System.Net;
	using System.Net.Http;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Web;
	using System.Web.Security;
	using DotNetOpenAuth.Configuration;
	using DotNetOpenAuth.Messaging;
	using DotNetOpenAuth.OAuth2.Messages;
	using Validation;

	/// <summary>
	/// An OAuth 2.0 consumer designed for web applications.
	/// </summary>
	public class WebServerClient : ClientBase {
		/// <summary>
		/// The cookie name for XSRF mitigation during authorization code grant flows.
		/// </summary>
		private const string XsrfCookieName = "DotNetOpenAuth.WebServerClient.XSRF-Session";

		/// <summary>
		/// Initializes a new instance of the <see cref="WebServerClient"/> class.
		/// </summary>
		/// <param name="authorizationServer">The authorization server.</param>
		/// <param name="clientIdentifier">The client identifier.</param>
		/// <param name="clientSecret">The client secret.</param>
		public WebServerClient(AuthorizationServerDescription authorizationServer, string clientIdentifier = null, string clientSecret = null)
			: this(authorizationServer, clientIdentifier, DefaultSecretApplicator(clientSecret)) {
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="WebServerClient"/> class.
		/// </summary>
		/// <param name="authorizationServer">The authorization server.</param>
		/// <param name="clientIdentifier">The client identifier.</param>
		/// <param name="clientCredentialApplicator">
		/// The tool to use to apply client credentials to authenticated requests to the Authorization Server.
		/// May be <c>null</c> for clients with no secret or other means of authentication.
		/// </param>
		public WebServerClient(AuthorizationServerDescription authorizationServer, string clientIdentifier, ClientCredentialApplicator clientCredentialApplicator)
			: base(authorizationServer, clientIdentifier, clientCredentialApplicator) {
		}

		/// <summary>
		/// Gets or sets an optional component that gives you greater control to record and influence the authorization process.
		/// </summary>
		/// <value>The authorization tracker.</value>
		public IClientAuthorizationTracker AuthorizationTracker { get; set; }

		/// <summary>
		/// Prepares a request for user authorization from an authorization server.
		/// </summary>
		/// <param name="scopes">The scope of authorized access requested.</param>
		/// <param name="returnTo">The URL the authorization server should redirect the browser (typically on this site) to when the authorization is completed.  If null, the current request's URL will be used.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <returns>
		/// The authorization request.
		/// </returns>
		public Task<HttpResponseMessage> PrepareRequestUserAuthorizationAsync(IEnumerable<string> scopes = null, Uri returnTo = null, CancellationToken cancellationToken = default(CancellationToken)) {
			var authorizationState = new AuthorizationState(scopes) {
				Callback = returnTo,
			};
			return this.PrepareRequestUserAuthorizationAsync(authorizationState, cancellationToken);
		}

		/// <summary>
		/// Prepares a request for user authorization from an authorization server.
		/// </summary>
		/// <param name="authorization">The authorization state to associate with this particular request.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <returns>
		/// The authorization request.
		/// </returns>
		public async Task<HttpResponseMessage> PrepareRequestUserAuthorizationAsync(IAuthorizationState authorization, CancellationToken cancellationToken) {
			Requires.NotNull(authorization, "authorization");
			RequiresEx.ValidState(authorization.Callback != null || (HttpContext.Current != null && HttpContext.Current.Request != null), MessagingStrings.HttpContextRequired);
			RequiresEx.ValidState(!string.IsNullOrEmpty(this.ClientIdentifier), Strings.RequiredPropertyNotYetPreset, "ClientIdentifier");

			if (authorization.Callback == null) {
				authorization.Callback = this.Channel.GetRequestFromContext().GetPublicFacingUrl()
					.StripMessagePartsFromQueryString(this.Channel.MessageDescriptions.Get(typeof(EndUserAuthorizationSuccessResponseBase), Protocol.Default.Version))
					.StripMessagePartsFromQueryString(this.Channel.MessageDescriptions.Get(typeof(EndUserAuthorizationFailedResponse), Protocol.Default.Version));
				authorization.SaveChanges();
			}

			var request = new EndUserAuthorizationRequestC(this.AuthorizationServer) {
				ClientIdentifier = this.ClientIdentifier,
				Callback = authorization.Callback,
			};
			request.Scope.ResetContents(authorization.Scope);

			// Mitigate XSRF attacks by including a state value that would be unpredictable between users, but
			// verifiable for the same user/session.
			// If the host is implementing the authorization tracker though, they're handling this protection themselves.
			Cookie cookie = null;
			if (this.AuthorizationTracker == null) {
				var context = this.Channel.GetHttpContext();

				string xsrfKey = MessagingUtilities.GetNonCryptoRandomDataAsBase64(16);
				cookie = new Cookie(XsrfCookieName, xsrfKey) {
					HttpOnly = true,
					Secure = FormsAuthentication.RequireSSL,
					////Expires = DateTime.Now.Add(OAuth2ClientSection.Configuration.MaxAuthorizationTime), // we prefer session cookies to persistent ones
				};
				request.ClientState = xsrfKey;
			}

			var response = await this.Channel.PrepareResponseAsync(request, cancellationToken);
			if (cookie != null) {
				response.Headers.SetCookie(cookie);
			}

			return response;
		}

		/// <summary>
		/// Processes the authorization response from an authorization server, if available.
		/// </summary>
		/// <param name="request">The incoming HTTP request that may carry an authorization response.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <returns>The authorization state that contains the details of the authorization.</returns>
		public async Task<IAuthorizationState> ProcessUserAuthorizationAsync(HttpRequestBase request = null, CancellationToken cancellationToken = default(CancellationToken)) {
			RequiresEx.ValidState(!string.IsNullOrEmpty(this.ClientIdentifier), Strings.RequiredPropertyNotYetPreset, "ClientIdentifier");
			RequiresEx.ValidState(this.ClientCredentialApplicator != null, Strings.RequiredPropertyNotYetPreset, "ClientCredentialApplicator");

			request = request ?? this.Channel.GetRequestFromContext();
			var response = await this.Channel.TryReadFromRequestAsync<IMessageWithClientState>(request.AsHttpRequestMessage(), cancellationToken);
			if (response != null) {
				Uri callback = request.GetPublicFacingUrl().StripMessagePartsFromQueryString(this.Channel.MessageDescriptions.Get(response));
				IAuthorizationState authorizationState;
				if (this.AuthorizationTracker != null) {
					authorizationState = this.AuthorizationTracker.GetAuthorizationState(callback, response.ClientState);
					ErrorUtilities.VerifyProtocol(authorizationState != null, ClientStrings.AuthorizationResponseUnexpectedMismatch);
				} else {
					var context = this.Channel.GetHttpContext();

					HttpCookie cookie = request.Cookies[XsrfCookieName];
					ErrorUtilities.VerifyProtocol(cookie != null && string.Equals(response.ClientState, cookie.Value, StringComparison.Ordinal), ClientStrings.AuthorizationResponseUnexpectedMismatch);
					authorizationState = new AuthorizationState { Callback = callback };
				}
				var success = response as EndUserAuthorizationSuccessAuthCodeResponse;
				var failure = response as EndUserAuthorizationFailedResponse;
				ErrorUtilities.VerifyProtocol(success != null || failure != null, MessagingStrings.UnexpectedMessageReceivedOfMany);
				if (success != null) {
					await this.UpdateAuthorizationWithResponseAsync(authorizationState, success, cancellationToken);
				} else { // failure
					Logger.OAuth.Info("User refused to grant the requested authorization at the Authorization Server.");
					authorizationState.Delete();
				}

				return authorizationState;
			}

			return null;
		}
	}
}
