// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>
/// The emoji the bar can type at the caret and the words that find them (port of upstream
/// CommandBarEmoji). .NET has no Unicode name table, so the set is a shipped list: the popular emoji
/// upstream leads with, then a broad set of everyday ones, with CLDR-style names and the intent words
/// people actually type.
/// </summary>
public static class CommandBarEmoji
{
    /// <param name="Character">What is typed.</param>
    /// <param name="Identity">Stable id (the character itself).</param>
    public sealed record Emoji(string Character, string Identity, string Name, string Keywords, bool AcceptsTone);

    /// <summary>The five skin tones and the yellow default. Values are the stored preference.</summary>
    public enum SkinTone { None, Light, MediumLight, Medium, MediumDark, Dark }

    public static string StorageValue(SkinTone tone) => tone switch
    {
        SkinTone.Light => "light",
        SkinTone.MediumLight => "mediumLight",
        SkinTone.Medium => "medium",
        SkinTone.MediumDark => "mediumDark",
        SkinTone.Dark => "dark",
        _ => string.Empty,
    };

    /// <summary>An unknown value reads as the default.</summary>
    public static SkinTone ParseTone(string? raw) => raw switch
    {
        "light" => SkinTone.Light,
        "mediumLight" => SkinTone.MediumLight,
        "medium" => SkinTone.Medium,
        "mediumDark" => SkinTone.MediumDark,
        "dark" => SkinTone.Dark,
        _ => SkinTone.None,
    };

    private static string? Modifier(SkinTone tone) => tone switch
    {
        SkinTone.Light => "\U0001F3FB",
        SkinTone.MediumLight => "\U0001F3FC",
        SkinTone.Medium => "\U0001F3FD",
        SkinTone.MediumDark => "\U0001F3FE",
        SkinTone.Dark => "\U0001F3FF",
        _ => null,
    };

    /// <summary>The same raised hand in each tone, for the picker.</summary>
    public static string Swatch(SkinTone tone) => Applying(tone, "✋");

    /// <summary>Variation selectors change presentation, not identity.</summary>
    public static string Canonical(string character) => character.Replace("️", string.Empty).Replace("︎", string.Empty);

    public static bool AcceptsSkinTone(string character)
    {
        var canonical = Canonical(character);
        return ToneBases.Contains(canonical);
    }

    /// <summary>The emoji wearing a tone (only single-scalar modifier bases take one).</summary>
    public static string Applying(SkinTone tone, string character)
    {
        var modifier = Modifier(tone);
        if (modifier is null || !AcceptsSkinTone(character)) return character;
        return Canonical(character) + modifier;
    }

    // Character | name | extra search words | "+" when it takes a skin tone.
    private const string Data = """
        😀|grinning face|smile happy
        😃|grinning face with big eyes|smile happy
        😄|grinning face with smiling eyes|smile happy laugh
        😁|beaming face with smiling eyes|grin happy
        😆|grinning squinting face|laugh happy
        😅|grinning face with sweat|relief nervous
        🤣|rolling on the floor laughing|haha lol rofl roflmao laugh laughing funny
        😂|face with tears of joy|haha lol roflmao laugh laughing tears funny
        🙂|slightly smiling face|smile
        🙃|upside-down face|sarcasm silly
        😉|winking face|wink
        😊|smiling face with smiling eyes|happy smile blush
        😍|smiling face with heart-eyes|love crush
        🥰|smiling face with hearts|love affection hearts
        😘|face blowing a kiss|kiss love
        😗|kissing face|kiss
        😜|winking face with tongue|silly joke
        🤪|zany face|crazy silly
        🤔|thinking face|think thinking hmm
        🤗|smiling face with open hands|hug
        🤩|star-struck|wow amazing
        🥳|partying face|party celebrate birthday
        😎|smiling face with sunglasses|cool sunglasses
        🤓|nerd face|geek
        😐|neutral face|meh
        😑|expressionless face|blank
        😶|face without mouth|silent
        🙄|face with rolling eyes|eyeroll whatever
        😏|smirking face|smirk
        😥|sad but relieved face|phew
        😮|face with open mouth|wow surprised
        😴|sleeping face|sleep tired zzz
        😌|relieved face|calm
        😔|pensive face|sad
        😪|sleepy face|tired
        🤤|drooling face|drool
        😭|loudly crying face|cry crying sad sob
        😢|crying face|sad tear
        😤|face with steam from nose|triumph angry
        😠|angry face|mad
        😡|enraged face|angry mad rage
        🤬|face with symbols on mouth|swear cursing angry
        🤯|exploding head|mind blown shocked
        😳|flushed face|embarrassed
        🥺|pleading face|please pleading puppy eyes
        😱|face screaming in fear|scream scared
        😨|fearful face|scared
        😰|anxious face with sweat|nervous
        💀|skull|dead death dying skeleton halloween
        ☠️|skull and crossbones|dead death danger poison pirate
        🙏|folded hands|appreciate please thanks thank thx you pray prayer high five|+
        👍|thumbs up|yes good approve like okay|+
        👎|thumbs down|no bad disapprove dislike|+
        👌|ok hand|okay perfect good|+
        🤌|pinched fingers|italian|+
        ✌️|victory hand|peace|+
        🤞|crossed fingers|luck hope|+
        🤟|love-you gesture|ily|+
        🤘|sign of the horns|rock metal|+
        👏|clapping hands|clap applause congrats congratulations|+
        🙌|raising hands|hooray celebrate praise|+
        👐|open hands|hands|+
        💪|flexed biceps|strong muscle|+
        🫶|heart hands|love heart hands|+
        👋|waving hand|wave hello hi bye|+
        🤝|handshake|deal agreement|+
        ✍️|writing hand|write|+
        💅|nail polish|nails beauty|+
        👀|eyes|look looking eyes see
        🧠|brain|smart think
        🫀|anatomical heart|organ
        🦾|mechanical arm|prosthetic robot
        🦿|mechanical leg|prosthetic robot
        👣|footprints|feet steps
        ❤️|red heart|love heart red
        🧡|orange heart|love
        💛|yellow heart|love
        💚|green heart|love
        💙|blue heart|love
        💜|purple heart|love
        🖤|black heart|love
        🤍|white heart|love
        💔|broken heart|heartbreak broken heart sad
        ❣️|heart exclamation|love
        💕|two hearts|love
        💞|revolving hearts|love
        🔥|fire|fire hot lit trending flame
        ✨|sparkles|sparkle sparkles magic clean
        ⭐|star|favorite
        🌟|glowing star|star shine
        💫|dizzy|star
        ⚡|high voltage|lightning zap electric
        ☀️|sun|sunny weather
        🌤️|sun behind small cloud|weather
        ☁️|cloud|weather
        🌧️|cloud with rain|rain weather
        ⛈️|cloud with lightning and rain|storm weather
        ❄️|snowflake|snow cold winter
        🎉|party popper|party celebrate celebration congrats congratulations
        🎊|confetti ball|party celebrate
        🎁|wrapped gift|present gift birthday
        🎂|birthday cake|birthday cake
        🍰|shortcake|cake dessert
        🍕|pizza|food
        🍔|hamburger|burger food
        🍟|french fries|fries food
        🌮|taco|food
        🍣|sushi|food
        🍎|red apple|apple fruit
        🍌|banana|fruit
        ☕|hot beverage|coffee tea
        🍺|beer mug|beer drink
        🍻|clinking beer mugs|cheers beer
        🥂|clinking glasses|cheers champagne toast
        🍷|wine glass|wine drink
        🧉|mate|drink
        🥤|cup with straw|soda drink
        🍫|chocolate bar|chocolate
        🍪|cookie|cookie
        🍩|doughnut|donut
        🥐|croissant|bread
        🧀|cheese wedge|cheese
        🚀|rocket|launch ship rocket fast
        ✈️|airplane|plane flight travel
        🚗|automobile|car
        🚕|taxi|cab
        🚲|bicycle|bike
        🛴|kick scooter|scooter
        🏠|house|home
        🏢|office building|work office
        🌍|globe showing europe-africa|world earth
        🌎|globe showing americas|world earth
        🌏|globe showing asia-australia|world earth
        🗺️|world map|map
        💻|laptop|computer pc
        🖥️|desktop computer|computer pc monitor
        ⌨️|keyboard|type
        🖱️|computer mouse|mouse
        📱|mobile phone|phone cell
        ⌚|watch|time
        🎧|headphone|music headphones
        📷|camera|photo
        🔋|battery|power
        💾|floppy disk|save
        🖨️|printer|print
        📡|satellite antenna|signal
        📁|file folder|folder
        📂|open file folder|folder
        📄|page facing up|document file
        📌|pushpin|pin
        📎|paperclip|attach
        🔖|bookmark|bookmark
        🔍|magnifying glass tilted left|search find
        🔒|locked|lock secure
        🔓|unlocked|unlock
        🔑|key|password
        🛠️|hammer and wrench|tools fix
        ⚙️|gear|settings cog
        ✅|check mark button|check done yes complete success
        ❌|cross mark|cross no wrong error fail
        ⚠️|warning|warning caution alert
        ❓|red question mark|question
        ❗|red exclamation mark|exclamation important
        💡|light bulb|idea lightbulb tip
        🔔|bell|notification
        🔕|bell with slash|mute silent
        ♻️|recycling symbol|recycle
        🆗|ok button|ok
        🆕|new button|new
        🔝|top arrow|top
        🐶|dog face|dog puppy pet
        🐱|cat face|cat kitten pet
        🐭|mouse face|mouse
        🐰|rabbit face|bunny rabbit
        🦊|fox|fox
        🐻|bear|bear
        🐼|panda|panda
        🐨|koala|koala
        🦁|lion|lion
        🐮|cow face|cow
        🐷|pig face|pig
        🐸|frog|frog
        🌱|seedling|plant grow
        🌲|evergreen tree|tree
        🌳|deciduous tree|tree
        🌴|palm tree|tree beach
        🌵|cactus|plant desert
        🌷|tulip|flower
        🌸|cherry blossom|flower spring
        🌹|rose|flower love
        🌺|hibiscus|flower
        🌻|sunflower|flower
        🍀|four leaf clover|luck
        🍁|maple leaf|autumn fall canada
        ⏰|alarm clock|alarm time
        ⏳|hourglass not done|wait time
        📅|calendar|date
        📈|chart increasing|growth up
        📉|chart decreasing|down
        📊|bar chart|chart stats
        💰|money bag|money rich
        💳|credit card|card pay
        🏆|trophy|win award
        🥇|1st place medal|gold first win
        🎯|bullseye|target goal
        🧩|puzzle piece|puzzle
        😇|smiling face with halo|angel innocent
        🥲|smiling face with tear|grateful
        😋|face savoring food|yum delicious
        😛|face with tongue|tongue
        🤑|money-mouth face|money rich
        🤭|face with hand over mouth|oops giggle
        🫡|saluting face|salute respect
        🤫|shushing face|quiet secret
        🤐|zipper-mouth face|secret quiet
        🤨|face with raised eyebrow|skeptical suspicious
        😬|grimacing face|awkward
        🤥|lying face|liar
        😷|face with medical mask|sick mask
        🤒|face with thermometer|sick ill
        🤕|face with head-bandage|hurt
        🤢|nauseated face|sick gross
        🤮|face vomiting|sick vomit
        🥵|hot face|hot sweat
        🥶|cold face|cold freezing
        🥴|woozy face|drunk dizzy
        😵|face with crossed-out eyes|dizzy dead
        🤠|cowboy hat face|cowboy
        🥸|disguised face|disguise
        😕|confused face|confused
        🙁|slightly frowning face|sad
        😟|worried face|worried
        😯|hushed face|surprised
        😲|astonished face|shocked
        😦|frowning face with open mouth|shocked
        😧|anguished face|anguish
        😩|weary face|tired
        😫|tired face|tired
        🥱|yawning face|bored tired
        😈|smiling face with horns|devil evil
        👿|angry face with horns|devil angry
        💩|pile of poo|poop
        🤡|clown face|clown
        👻|ghost|halloween boo
        👽|alien|ufo
        🤖|robot|bot ai
        😺|grinning cat|cat
        🙈|see-no-evil monkey|monkey shy
        🙉|hear-no-evil monkey|monkey
        🙊|speak-no-evil monkey|monkey oops
        💯|hundred points|100 perfect score
        💥|collision|boom explosion
        💦|sweat droplets|water
        💨|dashing away|fast wind
        💬|speech balloon|chat message comment
        💭|thought balloon|think
        💤|zzz|sleep
        ✋|raised hand|hand stop high five|+
        🖐️|hand with fingers splayed|hand|+
        🤚|raised back of hand|hand|+
        🖖|vulcan salute|spock|+
        👈|backhand index pointing left|left point|+
        👉|backhand index pointing right|right point|+
        👆|backhand index pointing up|up point|+
        👇|backhand index pointing down|down point|+
        ☝️|index pointing up|point up|+
        🫵|index pointing at the viewer|you point|+
        🤙|call me hand|call shaka|+
        ✊|raised fist|fist power|+
        👊|oncoming fist|punch fist bump|+
        🤛|left-facing fist|fist bump|+
        🤜|right-facing fist|fist bump|+
        🤲|palms up together|pray|+
        👂|ear|hear listen|+
        👃|nose|smell|+
        👶|baby|child|+
        🧒|child|kid|+
        👦|boy|kid|+
        👧|girl|kid|+
        🧑|person|adult|+
        👨|man|male|+
        👩|woman|female|+
        🧓|older person|old|+
        👴|old man|grandpa|+
        👵|old woman|grandma|+
        🙋|person raising hand|question hi|+
        🙇|person bowing|sorry thanks|+
        🤦|person facepalming|facepalm|+
        🤷|person shrugging|idk shrug whatever|+
        🏃|person running|run|+
        🚶|person walking|walk|+
        💃|woman dancing|dance|+
        🕺|man dancing|dance|+
        🧘|person in lotus position|yoga meditate|+
        🏋️|person lifting weights|gym workout|+
        👑|crown|king queen
        🎩|top hat|hat
        👓|glasses|glasses
        🕶️|sunglasses|cool
        👕|t-shirt|shirt clothes
        👟|running shoe|sneaker shoe
        🎒|backpack|school bag
        💼|briefcase|work business
        🌈|rainbow|pride
        🌙|crescent moon|moon night
        🌕|full moon|moon
        🌊|water wave|wave ocean sea
        💧|droplet|water drop
        ☔|umbrella with rain drops|rain
        ⛄|snowman without snow|snow winter
        🔆|bright button|brightness
        🍇|grapes|fruit
        🍉|watermelon|fruit
        🍊|tangerine|orange fruit
        🍋|lemon|fruit
        🍓|strawberry|fruit
        🍒|cherries|fruit
        🍑|peach|fruit
        🥑|avocado|fruit
        🥕|carrot|vegetable
        🌽|ear of corn|corn
        🌶️|hot pepper|spicy chili
        🥦|broccoli|vegetable
        🍞|bread|bread
        🥚|egg|egg
        🍳|cooking|egg breakfast
        🥓|bacon|breakfast
        🍗|poultry leg|chicken
        🌭|hot dog|food
        🥪|sandwich|food
        🍜|steaming bowl|ramen noodles
        🍝|spaghetti|pasta
        🍦|soft ice cream|ice cream dessert
        🍿|popcorn|movie
        🍵|teacup without handle|tea
        🧋|bubble tea|boba
        🍾|bottle with popping cork|champagne celebrate
        🍸|cocktail glass|cocktail drink
        ⚽|soccer ball|football sport
        🏀|basketball|sport
        🏈|american football|sport
        ⚾|baseball|sport
        🎾|tennis|sport
        🎮|video game|game controller gaming
        🎲|game die|dice
        ♟️|chess pawn|chess
        🎨|artist palette|art paint
        🎬|clapper board|movie film
        🎤|microphone|sing karaoke
        🎵|musical note|music
        🎶|musical notes|music
        🎸|guitar|music
        🎹|musical keyboard|piano music
        📚|books|read study
        📖|open book|read
        ✏️|pencil|write edit
        🖊️|pen|write
        📝|memo|note write
        📋|clipboard|paste
        🗑️|wastebasket|trash delete bin
        📦|package|box shipping
        ✉️|envelope|mail email
        📧|e-mail|email mail
        📨|incoming envelope|mail
        📞|telephone receiver|call phone
        🔗|link|url chain
        🧲|magnet|magnet
        🔧|wrench|fix tool
        🔨|hammer|tool build
        🪛|screwdriver|tool
        🧪|test tube|science lab
        💊|pill|medicine
        🩺|stethoscope|doctor
        🚑|ambulance|emergency
        🚨|police car light|alert siren
        🚦|vertical traffic light|traffic
        🛑|stop sign|stop
        🚧|construction|work in progress
        ⛽|fuel pump|gas
        🚂|locomotive|train
        🚌|bus|bus
        🚢|ship|boat
        ⛵|sailboat|boat
        🏖️|beach with umbrella|beach vacation
        🏔️|snow-capped mountain|mountain
        🏕️|camping|camp
        🗽|statue of liberty|new york
        🗼|tokyo tower|tokyo
        🎄|christmas tree|christmas xmas
        🎃|jack-o-lantern|halloween pumpkin
        🎆|fireworks|celebrate new year
        🎈|balloon|party
        🏁|chequered flag|finish race
        🚩|triangular flag|flag red flag
        🏳️|white flag|surrender
        ➕|plus|add
        ➖|minus|subtract
        ✖️|multiply|times
        ➗|divide|division
        ♾️|infinity|forever
        ‼️|double exclamation mark|exclamation
        ⁉️|exclamation question mark|interrobang
        ⭕|hollow red circle|circle
        🔴|red circle|red
        🟠|orange circle|orange
        🟡|yellow circle|yellow
        🟢|green circle|green
        🔵|blue circle|blue
        🟣|purple circle|purple
        ⚫|black circle|black
        ⚪|white circle|white
        🟥|red square|red
        🟩|green square|green
        🟦|blue square|blue
        ⬛|black large square|black
        ⬜|white large square|white
        ➡️|right arrow|right
        ⬅️|left arrow|left
        ⬆️|up arrow|up
        ⬇️|down arrow|down
        🔄|counterclockwise arrows button|refresh sync
        🔁|repeat button|repeat loop
        ▶️|play button|play
        ⏸️|pause button|pause
        ⏹️|stop button|stop
        ⏩|fast-forward button|forward
        🔊|speaker high volume|sound loud
        🔇|muted speaker|mute
        📢|loudspeaker|announcement
        ©️|copyright|copyright
        ®️|registered|registered
        ™️|trade mark|trademark
        🆘|sos button|help sos
        ℹ️|information|info
        """;

    private static readonly HashSet<string> ToneBases = new(StringComparer.Ordinal);

    /// <summary>Every emoji the bar offers, popular first. Parsed once.</summary>
    public static IReadOnlyList<Emoji> All { get; } = Load();

    private static List<Emoji> Load()
    {
        var list = new List<Emoji>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in Data.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split('|');
            if (parts.Length < 2) continue;
            var character = parts[0];
            if (!seen.Add(Canonical(character))) continue;
            var tone = parts.Length > 3 && parts[3] == "+";
            if (tone) ToneBases.Add(Canonical(character));
            list.Add(new Emoji(character, character, parts[1], parts.Length > 2 ? parts[2] : string.Empty, tone));
        }
        return list;
    }
}
