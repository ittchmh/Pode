using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace Pode
{
    public class PodeRequest : PodeProtocol, IDisposable
    {
        public EndPoint RemoteEndPoint { get; private set; }
        public EndPoint LocalEndPoint { get; private set; }
        public bool IsSsl { get; private set; }
        public bool SslUpgraded { get; private set; }
        public bool IsKeepAlive { get; protected set; }
        public virtual bool CloseImmediately { get => false; }

        public Stream InputStream { get; private set; }
        public X509Certificate Certificate { get; private set; }
        public bool AllowClientCertificate { get; private set; }
        public PodeTlsMode TlsMode { get; private set; }
        public X509Certificate2 ClientCertificate { get; set; }
        public SslPolicyErrors ClientCertificateErrors { get; set; }
        public SslProtocols Protocols { get; private set; }
        public HttpRequestException Error { get; set; }
        public bool IsAborted => (Error != default(HttpRequestException));
        public bool IsDisposed { get; private set; }

        private Socket Socket;
        protected PodeContext Context;
        protected static UTF8Encoding Encoding = new UTF8Encoding();

        private byte[] Buffer;
        private MemoryStream BufferStream;
        private const int BufferSize = 16384;

        public PodeRequest(Socket socket, PodeSocket podeSocket)
        {
            Socket = socket;
            RemoteEndPoint = socket.RemoteEndPoint;
            LocalEndPoint = socket.LocalEndPoint;
            TlsMode = podeSocket.TlsMode;
            Certificate = podeSocket.Certificate;
            IsSsl = (Certificate != default(X509Certificate));
            AllowClientCertificate = podeSocket.AllowClientCertificate;
            Protocols = podeSocket.Protocols;
        }

        public PodeRequest(PodeRequest request)
        {
            IsSsl = request.IsSsl;
            InputStream = request.InputStream;
            IsKeepAlive = request.IsKeepAlive;
            Socket = request.Socket;
            RemoteEndPoint = Socket.RemoteEndPoint;
            LocalEndPoint = Socket.LocalEndPoint;
            Error = request.Error;
            Context = request.Context;
            Certificate = request.Certificate;
            AllowClientCertificate = request.AllowClientCertificate;
            Protocols = request.Protocols;
            TlsMode = request.TlsMode;
        }

        public void Open()
        {
            // open the socket's stream
            InputStream = new NetworkStream(Socket, true);
            if (!IsSsl || TlsMode == PodeTlsMode.Explicit)
            {
                // if not ssl, use the main network stream
                return;
            }

            // otherwise, convert the stream to an ssl stream
            UpgradeToSSL();
        }

        public void UpgradeToSSL()
        {
            if (SslUpgraded)
            {
                return;
            }

            var ssl = new SslStream(InputStream, false, new RemoteCertificateValidationCallback(ValidateCertificateCallback));
            ssl.AuthenticateAsServerAsync(Certificate, AllowClientCertificate, Protocols, false).Wait(Context.Listener.CancellationToken);
            InputStream = ssl;
            SslUpgraded = true;
        }

        private bool ValidateCertificateCallback(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            ClientCertificateErrors = sslPolicyErrors;

            ClientCertificate = certificate == default(X509Certificate)
                ? default(X509Certificate2)
                : new X509Certificate2(certificate);

            return true;
        }

        protected async Task<int> BeginRead(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task<int>.Factory.FromAsync(InputStream.BeginRead, InputStream.EndRead, Buffer, 0, BufferSize, null);
        }

        public async Task<bool> Receive(CancellationToken cancellationToken)
        {
            try
            {
                Error = default(HttpRequestException);

                Buffer = new byte[BufferSize];
                BufferStream = new MemoryStream();

                var read = 0;
                var close = true;

                while ((read = await BeginRead(cancellationToken)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    BufferStream.Write(Buffer, 0, read);

                    if (Socket.Available > 0 || !ValidateInput(BufferStream.ToArray()))
                    {
                        continue;
                    }

                    var bytes = BufferStream.ToArray();
                    if (!Parse(bytes))
                    {
                        bytes = default(byte[]);
                        BufferStream.Dispose();
                        BufferStream = new MemoryStream();
                        continue;
                    }

                    bytes = default(byte[]);
                    close = false;
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                return close;
            }
            catch (HttpRequestException httpex)
            {
                Error = httpex;
            }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Error = new HttpRequestException(ex.Message, ex);
                Error.Data.Add("PodeStatusCode", 400);
            }
            finally
            {
                BufferStream.Dispose();
                Buffer = default(byte[]);
            }

            return false;
        }

        protected virtual bool Parse(byte[] bytes)
        {
            throw new NotImplementedException();
        }

        protected virtual bool ValidateInput(byte[] bytes)
        {
            return true;
        }

        public void SetContext(PodeContext context)
        {
            Context = context;
        }

        public virtual void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;

            if (Socket != default(Socket))
            {
                PodeSocket.CloseSocket(Socket);
            }

            if (InputStream != default(Stream))
            {
                InputStream.Dispose();
            }

            if (BufferStream != default(MemoryStream))
            {
                BufferStream.Dispose();
            }

            PodeHelpers.WriteErrorMessage($"Request disposed", Context.Listener, PodeLoggingLevel.Verbose, Context);
        }
    }
}