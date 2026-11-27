using System.Globalization;
using System.Text;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

internal static class ChannelRoomMappingProposer
{
    public static IReadOnlyDictionary<string, string> Propose(IReadOnlyList<OctorateChannelRoom> channelRooms, IReadOnlyList<OctorateRoom> products)
    {
        ArgumentNullException.ThrowIfNull(channelRooms);
        ArgumentNullException.ThrowIfNull(products);

        var productsByName = products
            .Select(product => (Id: product.Id, Name: Normalize(product.Name)))
            .Where(product => product.Name.Length > 0)
            .GroupBy(product => product.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(product => product.Id).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        var roomsByName = channelRooms
            .Where(room => room.Manageable && room.MappedProductId is null)
            .GroupBy(room => Normalize(room.Name), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0)
            .ToList();

        var proposals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in roomsByName)
        {
            if (group.Count() != 1)
                continue;

            if (productsByName.TryGetValue(group.Key, out var ids) && ids.Count == 1)
                proposals[group.First().ChannelRoomId] = ids[0];
        }

        return proposals;
    }

    internal static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var letters = new StringBuilder(name.Length);
        foreach (var character in name.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;

            letters.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        return string.Join(' ', letters.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
