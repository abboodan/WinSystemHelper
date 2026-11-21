using Xunit;

namespace WinSystemHelper.Tests;

public sealed class AuthorizationTests : TestWorkspace
{
    [Fact(DisplayName = "Administrator in a private chat is accepted")]
    public void AdministratorInAPrivateChatIsAccepted()
    {
        Assert.True(SenderAuthorization(nameof(AdministratorInAPrivateChatIsAccepted)));
    }

    [Fact(DisplayName = "Group member who is not an administrator is rejected")]
    public void GroupMemberWhoIsNotAnAdministratorIsRejected()
    {
        Assert.True(SenderAuthorization(nameof(GroupMemberWhoIsNotAnAdministratorIsRejected)));
    }

    [Fact(DisplayName = "Administrator writing in an allowlisted group is accepted")]
    public void AdministratorWritingInAnAllowlistedGroupIsAccepted()
    {
        Assert.True(SenderAuthorization(nameof(AdministratorWritingInAnAllowlistedGroupIsAccepted)));
    }

    [Fact(DisplayName = "Anonymous sender and unlisted chat are rejected")]
    public void AnonymousSenderAndUnlistedChatAreRejected()
    {
        Assert.True(SenderAuthorization(nameof(AnonymousSenderAndUnlistedChatAreRejected)));
    }

    private bool SenderAuthorization(string check)
    {
        long[] admins = [100, -500];
        bool observed = CommandPolicy.IsAuthorizedSender(100, 100, admins);
        if (check == nameof(AdministratorInAPrivateChatIsAccepted))
        {
            return observed;
        }
        observed = !CommandPolicy.IsAuthorizedSender(-500, 777, admins);
        if (check == nameof(GroupMemberWhoIsNotAnAdministratorIsRejected))
        {
            return observed;
        }
        observed = CommandPolicy.IsAuthorizedSender(-500, 100, admins);
        if (check == nameof(AdministratorWritingInAnAllowlistedGroupIsAccepted))
        {
            return observed;
        }
        return !CommandPolicy.IsAuthorizedSender(-500, null, admins) && !CommandPolicy.IsAuthorizedSender(999, 100, admins);
    }
}
