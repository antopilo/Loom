using System.Buffers.Text;
using System.Security.Cryptography;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>A static P-256 key pair of a host or a device, used for the secure channel's key agreement and the host's relay proof.</summary>
public sealed class RemoteKeys : IDisposable
{
	/// <summary>Stores the key for key agreement.</summary>
	private readonly ECDiffieHellman _agreement;

	/// <summary>Stores the same key for signatures.</summary>
	private readonly ECDsa _signing;

	/// <summary>Creates the key pair from a P-256 private key.</summary>
	private RemoteKeys( ECDiffieHellman agreement )
	{
		_agreement = agreement;
		_signing = ECDsa.Create( agreement.ExportParameters( true ) );
		PublicKey = agreement.ExportSubjectPublicKeyInfo();
		PublicKeyText = Base64Url.EncodeToString( PublicKey );
		HostId = ComputeHostId( PublicKey );
	}

	/// <summary>Gets the host's key file in the data folder; a bare host keeps its own in its folder.</summary>
	public static string DefaultHostPath => LoknPaths.Combine( "host-key.pem" );

	/// <summary>Gets this PC's device key file in the data folder, which it pairs with hosts.</summary>
	public static string DefaultDevicePath => LoknPaths.Combine( "device-key.pem" );

	/// <summary>Gets the public key as SPKI DER.</summary>
	public byte[] PublicKey
	{
		get;
	}

	/// <summary>Gets the public key as base64url SPKI, as frames carry it.</summary>
	public string PublicKeyText
	{
		get;
	}

	/// <summary>Gets the id derived from the public key, as <see cref="ComputeHostId"/>.</summary>
	public string HostId
	{
		get;
	}

	/// <summary>Gets the key for key agreement.</summary>
	internal ECDiffieHellman Agreement => _agreement;

	/// <summary>Creates a new key pair that lives in memory only.</summary>
	public static RemoteKeys Create() => new( ECDiffieHellman.Create( ECCurve.NamedCurves.nistP256 ) );

	/// <summary>Loads the key pair saved at <paramref name="path"/>, or creates and saves one when the file is missing.</summary>
	/// <exception cref="CryptographicException">The file holds no P-256 private key.</exception>
	public static RemoteKeys LoadOrCreate( string path )
	{
		if ( File.Exists( path ) )
		{
			var agreement = ECDiffieHellman.Create();
			try
			{
				agreement.ImportFromPem( File.ReadAllText( path ) );
				if ( !IsP256( agreement ) )
					throw new CryptographicException( $"{path} does not hold a P-256 key." );
				return new( agreement );
			}
			catch
			{
				agreement.Dispose();
				throw;
			}
		}

		var keys = Create();
		SecretFile.WriteAllText( path, keys._agreement.ExportPkcs8PrivateKeyPem() );
		return keys;
	}

	/// <summary>Derives a host id: the first 22 characters of base64url( SHA-256( SPKI ) ).</summary>
	public static string ComputeHostId( ReadOnlySpan<byte> spki ) => Base64Url.EncodeToString( SHA256.HashData( spki ) )[..22];

	/// <summary>Imports a base64url SPKI public key for key agreement, or null when it is not a P-256 key.</summary>
	public static ECDiffieHellman? ImportAgreementKey( string spki )
	{
		var key = ECDiffieHellman.Create();
		try
		{
			key.ImportSubjectPublicKeyInfo( Base64Url.DecodeFromChars( spki ), out var read );
			if ( read > 0 && IsP256( key ) )
				return key;
		}
		catch ( Exception exception ) when ( exception is FormatException or CryptographicException )
		{
		}
		key.Dispose();
		return null;
	}

	/// <summary>Signs data with ECDSA P-256 and SHA-256, in the IEEE P1363 format WebCrypto uses.</summary>
	public byte[] Sign( ReadOnlySpan<byte> data ) => _signing.SignData( data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation );

	/// <summary>Releases the keys.</summary>
	public void Dispose()
	{
		_agreement.Dispose();
		_signing.Dispose();
	}

	/// <summary>Checks that a key is on the P-256 curve; Windows names the curve rather than giving its OID, so both are accepted.</summary>
	private static bool IsP256( ECAlgorithm key )
	{
		var curve = key.ExportParameters( false ).Curve.Oid;
		return key.KeySize == 256 && (curve.Value == "1.2.840.10045.3.1.7" || curve.FriendlyName is "nistP256" or "ECDSA_P256" or "ECDH_P256");
	}
}
