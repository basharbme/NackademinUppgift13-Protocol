﻿using System;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BattleshipProtocol.Game;
using BattleshipProtocol.Game.Commands;
using BattleshipProtocol.Protocol;
using BattleshipProtocol.Protocol.Exceptions;
using BattleshipProtocol.Protocol.Internal;
using BattleshipProtocol.Protocol.Internal.Extensions;
using JetBrains.Annotations;

namespace BattleshipProtocol
{
    public class BattleGame : IDisposable
    {
        public const string ProtocolVersion = "BATTLESHIP/1.0";

        private readonly TcpClient _client;
        private CancellationTokenSource _disconnectTokenSource;
        private GameState _gameState;
        private bool _isLocalsTurn;

        /// <summary>
        /// Gets whether this application is the server.
        /// </summary>
        public bool IsHost { get; }

        /// <summary>
        /// Gets the local player object. That is- information about the player in this application.
        /// </summary>
        [NotNull]
        public Player LocalPlayer { get; }

        /// <summary>
        /// Gets the remote player object. That is- information about the other player.
        /// </summary>
        [NotNull]
        public Player RemotePlayer { get; }

        /// <summary>
        /// Gets or sets the state of the connection and game.
        /// </summary>
        public GameState GameState {
            get => _gameState;
            set {
                if (_gameState == value) return;
                _gameState = value;
                OnGameStateChanged();
            }
        }

        /// <summary>
        /// Called when the <see cref="GameState"/> property changes.
        /// </summary>
        public event EventHandler GameStateChanged;

        /// <summary>
        /// Gets or sets whether it's the local players turn. If not, it's the remote players turn.
        /// </summary>
        public bool IsLocalsTurn {
            get => _isLocalsTurn;
            set {
                if (_isLocalsTurn == value) return;
                _isLocalsTurn = value;
                OnLocalsTurnChanged();
            }
        }

        /// <summary>
        /// Called when the <see cref="IsLocalsTurn"/> property is changed.
        /// </summary>
        public event EventHandler LocalsTurnChanged;

        public PacketConnection PacketConnection { get; }

        private BattleGame(TcpClient client, PacketConnection packetConnection, Board localBoard, string playerName, bool isHost)
        {
            IsHost = isHost;
            RemotePlayer = new Player(false, client.Client.RemoteEndPoint);
            LocalPlayer = new Player(true, client.Client.LocalEndPoint)
            { Name = playerName, Board = localBoard };

            _client = client;
            PacketConnection = packetConnection;

            packetConnection.RegisterCommand(new FireCommand(this));
            packetConnection.RegisterCommand(new HelloCommand(this));
            packetConnection.RegisterCommand(new HelpCommand());
            packetConnection.RegisterCommand(new StartCommand(this));
            packetConnection.RegisterCommand(new QuitCommand(this));

            ForwardErrorsObserver.SubscribeTo(this);
            PacketConnection.Subscribe(new DisconnectedObserver(this));

            PacketConnection.BeginListening();
            GameState = GameState.Handshake;
        }

        /// <summary>
        /// Shoots at a given <paramref name="coordinate"/> parameter via the <see cref="FireCommand"/> command.
        /// The response will follow in a response packet and be handled automatically by <see cref="FireCommand"/>.
        /// </summary>
        /// <param name="coordinate">The coordinate to shoot at.</param>
        /// <param name="message">The optional message to append to the command.</param>
        /// <exception cref="InvalidOperationException">Not in <see cref="Protocol.GameState.InGame"/> state. Use <see cref="BattleGame.GameState"/>.</exception>
        /// <exception cref="InvalidOperationException">It's not your turn. Use <see cref="IsLocalsTurn"/>.</exception>
        /// <exception cref="InvalidOperationException">No FIRE command has been registered.</exception>
        /// <exception cref="InvalidOperationException">A FIRE command is already pending.</exception>
        /// <exception cref="ArgumentException">Coordinate has already been shot at.</exception>
        public async Task ShootAtAsync(Coordinate coordinate, [CanBeNull] string message = null)
        {
            if (GameState != GameState.InGame)
                throw new InvalidOperationException("You can only FIRE when in-game.");
            if (!IsLocalsTurn)
                throw new InvalidOperationException("You can only FIRE when it's your turn.");
            if (RemotePlayer.Board.IsShotAt(coordinate))
                throw new ArgumentException("Coordinate has already been shot at.", nameof(coordinate));

            var fireCommand = PacketConnection.GetCommand<FireCommand>();
            if (fireCommand is null)
                throw new InvalidOperationException("No FIRE command has been registered.");

            if (fireCommand.WaitingForResponseAt.HasValue)
                throw new InvalidOperationException("A FIRE command is already pending. Awaiting response...");

            fireCommand.WaitingForResponseAt = coordinate;

            message = message?.Trim();
            if (string.IsNullOrEmpty(message))
                await PacketConnection.SendCommandAsync<FireCommand>(coordinate.ToString());
            else
                await PacketConnection.SendCommandAsync<FireCommand>($"{coordinate} {message}");
        }

        /// <summary>
        /// Sends a start game message.
        /// The response will follow in a response packet and be handled automatically by <see cref="StartCommand"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">Not in <see cref="Protocol.GameState.Idle"/> state. Use <see cref="BattleGame.GameState"/>.</exception>
        /// <exception cref="InvalidOperationException">You're not the client. Only client can start the game.</exception>
        /// <exception cref="InvalidOperationException">No START command has been registered.</exception>
        public async Task StartGameAsync()
        {
            if (IsHost)
                throw new InvalidOperationException("Game can only be started from the client.");
            if (GameState != GameState.Idle)
                throw new InvalidOperationException("You can only start a game while in idle state.");

            var startCommand = PacketConnection.GetCommand<StartCommand>();
            if (startCommand is null)
                throw new InvalidOperationException("No START command has been registered.");

            await PacketConnection.SendCommandAsync<StartCommand>();
        }

        /// <summary>
        /// Disconnects from the remote by sending a disconnected command,
        /// with a timeout if the remote doesn't respond with a disconnected response.
        /// </summary>
        /// <param name="timeout">Timeout in milliseconds.</param>
        public async Task Disconnect(int timeout = 10_000)
        {
            if (GameState == GameState.Disconnected)
                throw new ObjectDisposedException(nameof(PacketConnection), "Game is already disconnected.");

            if (!PacketConnection.IsConnected)
                throw new ObjectDisposedException(nameof(PacketConnection), "Stream is already closed.");

            GameState = GameState.Disconnected;

            if (IsHost)
            {
                await PacketConnection.SendResponseAsync(ResponseCode.ConnectionClosed, "Connection closed");
                Dispose();
            }
            else
            {
                _disconnectTokenSource = new CancellationTokenSource();
                await PacketConnection.SendCommandAsync<QuitCommand>();

                await Task.Delay(timeout, _disconnectTokenSource.Token)
                    .ContinueWith(t =>
                    {
                        if (PacketConnection.IsConnected)
                            Dispose();
                    });
            }
        }


        /// <summary>
        /// <para>
        /// Connects to a host at a given address and completes the version handshake. Supports both IPv4 and IPv6, given it is enabled on the OS.
        /// </para>
        /// <para>
        /// On connection error, use <see cref="SocketException.ErrorCode"/> from the thrown error to obtain the cause of the error.
        /// Refer to the <see href="https://docs.microsoft.com/en-us/windows/desktop/winsock/windows-sockets-error-codes-2">Windows Sockets version 2 API error code</see> documentation.
        /// </para>
        /// <para>
        /// On packet error, use <see cref="ProtocolException.ErrorMessage"/> from the thrown error to obtain the cause of the error. 
        /// </para>
        /// </summary>
        /// <param name="address">Host name or IP address.</param>
        /// <param name="port">Host port.</param>
        /// <param name="localBoard">The board of the local player.</param>
        /// <param name="localPlayerName">The name of the local player.</param>
        /// <param name="timeout">Timeout in milliseconds for awaiting version handshake.</param>
        /// <exception cref="SocketException">An error occurred when accessing the socket.</exception>
        /// <exception cref="ArgumentNullException">The <paramref name="address"/> parameter is null.</exception>
        /// <exception cref="ProtocolException">Version handshake failed.</exception>
        /// <exception cref="SocketException">An error occurred when accessing the socket.</exception>
        /// <exception cref="ArgumentNullException">The <paramref name="localPlayerName"/> parameter is null or whitespace.</exception>
        /// <exception cref="ArgumentException">The ships in the <paramref name="localBoard"/> parameter is not set up.</exception>
        [NotNull]
        public static Task<BattleGame> ConnectAsync([NotNull] string address, ushort port,
            [NotNull] Board localBoard, [NotNull] string localPlayerName, int timeout = 10_000)
        {
            return ConnectAsync(new ConnectionSettings
            {
                Address = address,
                Port = port,
            }, localBoard, localPlayerName, new CancellationTokenSource(timeout).Token);
        }

        /// <summary>
        /// <para>
        /// Connects to a host at a given address and completes the version handshake. Supports both IPv4 and IPv6, given it is enabled on the OS.
        /// </para>
        /// <para>
        /// On connection error, use <see cref="SocketException.ErrorCode"/> from the thrown error to obtain the cause of the error.
        /// Refer to the <see href="https://docs.microsoft.com/en-us/windows/desktop/winsock/windows-sockets-error-codes-2">Windows Sockets version 2 API error code</see> documentation.
        /// </para>
        /// <para>
        /// On packet error, use <see cref="ProtocolException.ErrorMessage"/> from the thrown error to obtain the cause of the error. 
        /// </para>
        /// </summary>
        /// <param name="connectionSettings">The settings for the connection.</param>
        /// <param name="localBoard">The board of the local player.</param>
        /// <param name="localPlayerName">The name of the local player.</param>
        /// <param name="cancellationToken">Cancellation token to cancel this action.</param>
        /// <exception cref="SocketException">An error occurred when accessing the socket.</exception>
        /// <exception cref="ProtocolException">Version handshake failed.</exception>
        /// <exception cref="SocketException">An error occurred when accessing the socket.</exception>
        /// <exception cref="ArgumentNullException">The <paramref name="localPlayerName"/> parameter is null or whitespace.</exception>
        /// <exception cref="ArgumentException">The ships in the <paramref name="localBoard"/> parameter is not set up.</exception>
        [NotNull]
        public static async Task<BattleGame> ConnectAsync(ConnectionSettings connectionSettings,
            [NotNull] Board localBoard, [NotNull] string localPlayerName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(localPlayerName))
                throw new ArgumentNullException(nameof(localPlayerName), "Name of local player must be set.");

            if (!localBoard.Ships.All(s => s.IsOnBoard))
                throw new ArgumentException("Local board is not set up!", nameof(localBoard));
            
            cancellationToken.ThrowIfCancellationRequested();

            TcpClient tcp = null;
            try
            {
                tcp = new TcpClient();

                await tcp.ConnectAsync(connectionSettings.Address, connectionSettings.Port);
                var connection = new PacketConnection(tcp.GetStream(), connectionSettings.Encoding,
                    connectionSettings.DetectEncodingFromBOM);

                await connection.EnsureVersionGreeting(ProtocolVersion, cancellationToken);

                var game = new BattleGame(tcp, connection, localBoard, localPlayerName, isHost: false);

                await game.PacketConnection.SendCommandAsync<HelloCommand>(localPlayerName);

                return game;
            }
            catch
            {
                tcp?.Dispose();
                throw;
            }
        }

        /// <summary>
        /// <para>
        /// Host on a given port. Will return once a client has connected.
        /// </para>
        /// <para>
        /// On connection error, use <see cref="SocketException.ErrorCode"/> from the thrown error to obtain the cause of the error.
        /// Refer to the <see href="https://docs.microsoft.com/en-us/windows/desktop/winsock/windows-sockets-error-codes-2">Windows Sockets version 2 API error code</see> documentation.
        /// </para>
        /// </summary>
        /// <param name="port">Host port.</param>
        /// <param name="localBoard">The board of the local player.</param>
        /// <param name="localPlayerName">The name of the local player.</param>
        /// <param name="cancellationToken">Cancellation token to cancel this action.</param>
        /// <exception cref="SocketException">An error occurred when accessing the socket.</exception>
        /// <exception cref="ArgumentNullException">The <paramref name="localPlayerName"/> parameter is null or whitespace.</exception>
        /// <exception cref="ArgumentException">The ships in the <paramref name="localBoard"/> parameter is not set up.</exception>
        [NotNull]
        public static Task<BattleGame> HostAndWaitAsync(ushort port,
            [NotNull] Board localBoard, [NotNull] string localPlayerName, CancellationToken cancellationToken)
        {
            return HostAndWaitAsync(new ConnectionSettings
            {
                Port = port,
            }, localBoard, localPlayerName, cancellationToken);
        }

        /// <summary>
        /// <para>
        /// Host on a given port. Will return once a client has connected.
        /// </para>
        /// <para>
        /// On connection error, use <see cref="SocketException.ErrorCode"/> from the thrown error to obtain the cause of the error.
        /// Refer to the <see href="https://docs.microsoft.com/en-us/windows/desktop/winsock/windows-sockets-error-codes-2">Windows Sockets version 2 API error code</see> documentation.
        /// </para>
        /// </summary>
        /// <param name="port">Host port.</param>
        /// <param name="localBoard">The board of the local player.</param>
        /// <param name="localPlayerName">The name of the local player.</param>
        /// <exception cref="SocketException">An error occurred when accessing the socket.</exception>
        /// <exception cref="ArgumentNullException">The <paramref name="localPlayerName"/> parameter is null or whitespace.</exception>
        /// <exception cref="ArgumentException">The ships in the <paramref name="localBoard"/> parameter is not set up.</exception>
        [NotNull]
        public static Task<BattleGame> HostAndWaitAsync(ushort port,
            [NotNull] Board localBoard, [NotNull] string localPlayerName)
        {
            return HostAndWaitAsync(new ConnectionSettings
            {
                Port = port,
            }, localBoard, localPlayerName);
        }

        /// <summary>
        /// <para>
        /// Host on a given port. Will return once a client has connected.
        /// </para>
        /// <para>
        /// On connection error, use <see cref="SocketException.ErrorCode"/> from the thrown error to obtain the cause of the error.
        /// Refer to the <see href="https://docs.microsoft.com/en-us/windows/desktop/winsock/windows-sockets-error-codes-2">Windows Sockets version 2 API error code</see> documentation.
        /// </para>
        /// </summary>
        /// <param name="connectionSettings">The settings for connecting. The <see cref="ConnectionSettings.Address"/> property is ignored.</param>
        /// <param name="localBoard">The board of the local player.</param>
        /// <param name="localPlayerName">The name of the local player.</param>
        /// <param name="cancellationToken">Cancellation token to cancel this action.</param>
        /// <exception cref="SocketException">An error occurred when accessing the socket.</exception>
        /// <exception cref="ArgumentNullException">The <paramref name="localPlayerName"/> parameter is null or whitespace.</exception>
        /// <exception cref="ArgumentException">The ships in the <paramref name="localBoard"/> parameter is not set up.</exception>
        [NotNull]
        public static async Task<BattleGame> HostAndWaitAsync(ConnectionSettings connectionSettings,
            [NotNull] Board localBoard, [NotNull] string localPlayerName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(localPlayerName))
                throw new ArgumentNullException(nameof(localPlayerName), "Name of local player must be set.");

            if (!localBoard.Ships.All(s => s.IsOnBoard))
                throw new ArgumentException("Local board is not set up!", nameof(localBoard));

            cancellationToken.ThrowIfCancellationRequested();

            TcpListener listener = TcpListener.Create(connectionSettings.Port);
            try
            {
                listener.Start();
                using (cancellationToken.Register(() => listener.Stop()))
                {
                    TcpClient tcp = await Task.Run(() => listener.AcceptTcpClientAsync(), cancellationToken);
                    var connection = new PacketConnection(tcp.GetStream(), connectionSettings.Encoding,
                        connectionSettings.DetectEncodingFromBOM);

                    await connection.SendResponseAsync(new Response
                    {
                        Code = ResponseCode.VersionGreeting,
                        Message = ProtocolVersion
                    });

                    return new BattleGame(tcp, connection, localBoard, localPlayerName, true);
                }
            }
            catch (Exception e) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Listening for TCP/IP connection was cancelled.", e);
            }
            finally
            {
                listener.Stop();
            }
        }

        public virtual void Dispose()
        {
            _client.Dispose();
        }

        private class DisconnectedObserver : IObserver<IPacket>
        {
            private readonly BattleGame _game;

            public DisconnectedObserver(BattleGame game)
            {
                _game = game;
            }

            public void OnNext(IPacket value)
            {
            }

            public void OnError(Exception error)
            {
            }

            public void OnCompleted()
            {
                _game.GameState = GameState.Disconnected;
                _game._disconnectTokenSource?.Cancel();
            }
        }

        protected virtual void OnGameStateChanged()
        {
            GameStateChanged?.Invoke(this, EventArgs.Empty);
        }

        protected virtual void OnLocalsTurnChanged()
        {
            LocalsTurnChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}