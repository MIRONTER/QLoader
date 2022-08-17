# generate NetSparkle appcast items

import sys
import requests
from datetime import datetime

release_tag = input("Enter release tag: ")
version_string = release_tag.split("-")[0]
version_code = input(f"Enter version code [{version_string[1:]}.1]: ") or f"{version_string[1:]}.1"


# ask if update is critical
while True:
    critical = input("Is this a critical update? (y/N): ")
    if critical.lower() == "y":
        critical = True
        break
    elif critical.lower() == "n" or critical == "":
        critical = False
        break
    else:
        print("Invalid input")
critical = "true" if critical else "false"

release_url = f'https://github.com/QLoaderFiles/releases/tag/{release_tag}'
release_api_url = f'https://api.github.com/repos/skrimix/QLoaderFiles/releases/tags/{release_tag}'

# check if release tag exists
response = requests.get(release_api_url)
if response.status_code == 404:
    print("Release tag does not exist")
    sys.exit(1)
if response.status_code != 200:
    print("Error checking release tag")
    sys.exit(1)

# get and parse published date
published_date = datetime.strptime(response.json()["published_at"], "%Y-%m-%dT%H:%M:%SZ")

# format date in "ddd, dd MMM yyyy HH:mm:ss zzz" format
published_date_string = published_date.strftime("%a, %d %b %Y %H:%M:%S +0000")


# generate linux-x64 appcast item
linux_x64_file_length = next(asset["size"] for asset in response.json()["assets"] if asset["name"] == "linux-x64.tar.gz")
linux_x64_appcast_item = f"""<item>
    <title>Version {version_string[1:]} Linux</title>
    <sparkle:releaseNotesLink>
    https://github.com/skrimix/QLoaderFiles/raw/master/release_notes/{version_string}.md
    </sparkle:releaseNotesLink>
    <pubDate>{published_date_string}</pubDate>
    <enclosure url="https://github.com/skrimix/QLoaderFiles/releases/download/{release_tag}/linux-x64.tar.gz"
                sparkle:version="{version_code}"
                sparkle:os="linux"
                sparkle:criticalUpdate="{critical}"
                length="{linux_x64_file_length}"
                type="application/octet-stream" />
</item>"""

# generate linux-arm64 appcast item
linux_arm64_file_length = next(asset["size"] for asset in response.json()["assets"] if asset["name"] == "linux-arm64.tar.gz")
linux_arm64_appcast_item = f"""<item>
    <title>Version {version_string[1:]} Linux arm64</title>
    <sparkle:releaseNotesLink>
    https://github.com/skrimix/QLoaderFiles/raw/master/release_notes/{version_string}.md
    </sparkle:releaseNotesLink>
    <pubDate>{published_date_string}</pubDate>
    <enclosure url="https://github.com/skrimix/QLoaderFiles/releases/download/{release_tag}/linux-arm64.tar.gz"
                sparkle:version="{version_code}"
                sparkle:os="linux"
                sparkle:criticalUpdate="{critical}"
                length="{linux_x64_file_length}"
                type="application/octet-stream" />
</item>"""

# generate osx-x64 appcast item
osx_x64_file_length = next(asset["size"] for asset in response.json()["assets"] if asset["name"] == "osx-x64.zip")
osx_x64_appcast_item = f"""<item>
    <title>Version {version_string[1:]} Mac</title>
    <sparkle:releaseNotesLink>
    https://github.com/skrimix/QLoaderFiles/raw/master/release_notes/{version_string}.md
    </sparkle:releaseNotesLink>
    <pubDate>{published_date_string}</pubDate>
    <enclosure url="https://github.com/skrimix/QLoaderFiles/releases/download/{release_tag}/osx-x64.zip"
                sparkle:version="{version_code}"
                sparkle:os="osx"
                sparkle:criticalUpdate="{critical}"
                length="{osx_x64_file_length}"
                type="application/octet-stream" />
</item>"""

# print appcast items
print("appcast.xml items:")
print(linux_x64_appcast_item)
print(osx_x64_appcast_item)

print("\nappcast_arm64.xml items:")
print(linux_arm64_appcast_item)