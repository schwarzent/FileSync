using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileSync.Core;
using FileSync.Security;

namespace FileSync.Network;

public sealed class Pairing(StateStore store, DeviceIdentity identity, PeerClient client)
{
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromMinutes(5);
    private readonly object gate = new();
    private byte[]? secretHash;
    private DateTime expires;

    public object Create(string address, int port)
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        lock (gate)
        {
            secretHash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
            expires = DateTime.UtcNow.Add(InviteLifetime);
        }
        var invite = new Invite(NetworkPolicy.Address(address).ToString(), port, identity.Id, secret);
        return new { code = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(invite, JsonFiles.Options)), expiresUtc = expires };
    }

    public PairResponse Accept(PairRequest request, string fingerprint, string address, int ownPort)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80 || request.Secret?.Length != 64)
            throw new UserError("配对请求无效。");
        NetworkPolicy.Address(address);
        NetworkPolicy.Port(request.Port);
        lock (gate)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(request.Secret));
            if (secretHash is null || DateTime.UtcNow > expires || !CryptographicOperations.FixedTimeEquals(hash, secretHash))
                throw new UserError("配对邀请无效、已使用或已过期。", 403);
            var peer = new Peer(fingerprint, request.Name.Trim(), address, request.Port);
            if (peer.Id == identity.Id) throw new UserError("不能与本机配对。");
            store.Update(s => s with { Peers = s.Peers.Where(p => p.Id != peer.Id).Append(peer).ToArray() });
            secretHash = null;
        }
        return new(store.Current.Name, identity.Id, ownPort);
    }

    public async Task<Peer> Join(string code, int ownPort, CancellationToken token)
    {
        Invite invite;
        try
        {
            invite = JsonSerializer.Deserialize<Invite>(Convert.FromBase64String(code.Trim()), JsonFiles.Options)
                ?? throw new FormatException();
        }
        catch (Exception error) when (error is FormatException or JsonException)
        { throw new UserError("配对邀请格式无效。"); }
        if (invite.Fingerprint?.Length != 64 || invite.Secret?.Length != 64) throw new UserError("配对邀请无效。");
        using var connection = client.Connect(invite.Address, invite.Port, invite.Fingerprint);
        using var response = await connection.PostAsJsonAsync("peer/pair",
            new PairRequest(store.Current.Name, ownPort, invite.Secret), token);
        await PeerClient.Check(response, token);
        var paired = await response.Content.ReadFromJsonAsync<PairResponse>(token) ?? throw new UserError("配对响应为空。");
        if (paired.Fingerprint != invite.Fingerprint) throw new UserError("配对证书与邀请不一致。");
        var peer = new Peer(paired.Fingerprint, paired.Name, invite.Address, invite.Port);
        store.Update(s => s with { Peers = s.Peers.Where(p => p.Id != peer.Id).Append(peer).ToArray() });
        return peer;
    }
}
