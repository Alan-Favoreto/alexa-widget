namespace AlexaWidget.Tests;

public class LoginCallbackTests
{
    const string Callback = "https://www.amazon.com.br/ap/maplanding";

    [Theory]
    [InlineData(Callback + "?openid.mode=id_res&openid.oa2.authorization_code=ANabc123", "ANabc123")]
    [InlineData(Callback + "/?openid.oa2.authorization_code=X1", "X1")]
    [InlineData("HTTPS://WWW.AMAZON.COM.BR/ap/maplanding?openid.oa2.authorization_code=Y2", "Y2")]
    public void ReadsTheCode(string address, string expected)
    {
        Assert.True(LoginCallback.TryRead(address, Callback, out var code, out var error));
        Assert.Equal(expected, code);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(Callback + "?openid.mode=cancel", "O login foi cancelado.")]
    [InlineData(Callback + "?error=access_denied&error_description=Negado", "A Amazon recusou o login: Negado")]
    [InlineData(Callback + "?error=access_denied", "A Amazon recusou o login: access_denied")]
    [InlineData(Callback, "A Amazon não devolveu o código de login. Tente entrar de novo.")]
    public void ExplainsWhyThereIsNoCode(string address, string expected)
    {
        Assert.True(LoginCallback.TryRead(address, Callback, out var code, out var error));
        Assert.Null(code);
        Assert.Equal(expected, error);
    }

    [Theory]
    [InlineData(Callback + "-evil?openid.oa2.authorization_code=X")]
    [InlineData("http://www.amazon.com.br/ap/maplanding?openid.oa2.authorization_code=X")]
    [InlineData("https://www.amazon.com.br.evil.com/ap/maplanding?openid.oa2.authorization_code=X")]
    [InlineData("https://www.amazon.com/ap/maplanding?openid.oa2.authorization_code=X")]
    [InlineData("https://www.amazon.com.br/ap/signin?openid.return_to=https%3A%2F%2Fwww.amazon.com.br%2Fap%2Fmaplanding")]
    [InlineData("não é um endereço")]
    [InlineData("")]
    public void IgnoresAnythingButTheExactCallback(string address)
    {
        Assert.False(LoginCallback.TryRead(address, Callback, out var code, out var error));
        Assert.Null(code);
        Assert.Null(error);
    }
}
