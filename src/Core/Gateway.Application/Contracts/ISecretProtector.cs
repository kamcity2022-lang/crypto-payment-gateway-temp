namespace Gateway.Application.Contracts;

public interface ISecretProtector
{
    string Encrypt(string plainText);
    string Decrypt(string cipherText);
}