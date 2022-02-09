﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DotNetReleaser.Changelog;
using DotNetReleaser.Configuration;
using DotNetReleaser.Helpers;
using DotNetReleaser.Logging;
using NuGet.Versioning;
using Octokit;

namespace DotNetReleaser.DevHosting;

internal class GitHubDevHosting : IDevHosting
{
    private readonly ISimpleLogger _log;
    private readonly string _url;
    private readonly string _apiToken;
    private readonly GitHubClient _client;
    
    public GitHubDevHosting(ISimpleLogger log, DevHostingConfiguration hostingConfiguration, string apiToken)
    {
        Logger = log;
        Configuration = hostingConfiguration;
        _log = log;
        _url = hostingConfiguration.Base;
        _apiToken = apiToken;
        _client = new GitHubClient(new ProductHeaderValue(nameof(ReleaserApp)), new Uri(hostingConfiguration.Api));
    }

    public ISimpleLogger Logger { get; }

    public DevHostingConfiguration Configuration { get; }

    public async Task<bool> Connect()
    {
        var tokenAuth = new Credentials(_apiToken); // NOTE: not real token
        _client.Credentials = tokenAuth;

        _log.Info("Connecting to GitHub");
        try
        {
            _ = await _client.User.Current();
        }
        catch (Exception ex)
        {
            _log.Error($"Unable to connect GitHub. Reason: {ex.Message}");
            return false;
        }
        return true;
    }

    public async Task<ChangelogCollection?> GetChanges(string user, string repo, string versionPrefix, string version)
    {
        //var info.Version
        //_config.GitHub.VersionPrefix

        _log.Info($"Building Changelog: collecting commits and PR for version {version}");
        
        var tags = await _client.Repository.GetAllTags(user, repo);
        var regex = new Regex(@$"{versionPrefix}(\d+(\.\d+)+.*)");
        var versions = new List<(RepositoryTag, NuGetVersion)>();
        NuGetVersion? versionForCurrent = null;
        foreach (var tag in tags)
        {
            var match = regex.Match(tag.Name);
            if (match.Success && NuGetVersion.TryParse(match.Groups[1].Value, out var nubGetVersion))
            {
                var tagVersion = match.Groups[1].Value.Trim();
                if (tagVersion.Equals(version, StringComparison.OrdinalIgnoreCase))
                {
                    versionForCurrent = nubGetVersion;
                }
                versions.Add((tag, nubGetVersion));
            }
        }

        var versionForPrevious = versions.Select(x => x.Item2).Where(x => x != versionForCurrent).Max();
        var shaForPrevious = versions.FirstOrDefault(x => x.Item2 == versionForPrevious).Item1?.Commit?.Sha;
        var shaForCurrent = versions.FirstOrDefault(x => x.Item2 == versionForCurrent).Item1?.Commit?.Sha;

        if (versionForPrevious is null)
        {
            var commits = await _client.Repository.Commit.GetAll(user, repo, new ApiOptions() { PageSize = 1 });

            var firstCommit = commits.FirstOrDefault(x => x.Parents.Count == 0);
            if (firstCommit is null)
            {
                _log.Error("Changelog cannot be generated without an initial commit");
                return null;
            }
            shaForPrevious = firstCommit.Sha;
        }

        if (shaForCurrent is null)
        {
            var head = await _client.Repository.Commit.Get(user, repo, "HEAD");
            if (head is null)
            {
                _log.Error("Unable to find a commit for head");
                return null;
            }

            shaForCurrent = head.Sha;
        }


        // If we have same sha for the same version, then it could be that the current version hasn't been updated
        // in that case, we can't generate a changelog
        if (shaForCurrent == shaForPrevious || shaForPrevious is null)
        {
            _log.Warn($"No changes in changelog for {version}");
            return null;
        }

        if (versionForCurrent is null)
        {
            _log.Error($"Unable to find the tag for the version {version}");
            return null;
        }

        var compareCommits = await _client.Repository.Commit.Compare(user, repo, shaForPrevious, shaForCurrent);

        var shaCommitsToExclude = new List<string>();

        var allCommits = new List<GitHubCommit>();
        var changeLogCollection = new ChangelogCollection();

        foreach (var commit in compareCommits.Commits)
        {
            // Query if the sha commit is a coming from a PR
            var addr = new Uri($"{_client.BaseAddress}repos/{user}/{repo}/commits/{commit.Sha}/pulls");
            var result = await _client.Connection.Get<object>(addr, TimeSpan.FromSeconds(10));

            int prNumber = -1;
            if (result.HttpResponse.StatusCode == HttpStatusCode.OK)
            {
                // The commit is a merge commit/PR
                if (result.HttpResponse.ContentType.Contains("json") && result.HttpResponse.Body is string text)
                {
                    var array = JsonNode.Parse(text) as JsonArray;
                    if (array is not null)
                    {
                        foreach (var item in array.OfType<JsonObject>())
                        {
                            if (item.TryGetPropertyValue("merge_commit_sha", out var mergeCommitSha) && mergeCommitSha is not null)
                            {
                                if (string.Equals(commit.Sha, mergeCommitSha.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                                {
                                    if (item.TryGetPropertyValue("number", out var prNumberAsText) && prNumberAsText is not null)
                                    {
                                        prNumber = prNumberAsText.GetValue<int>();
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (prNumber > 0)
            {
                // Merge pull request #
                var pr = await _client.PullRequest.Get(user, repo, prNumber);
                var commitsInPr = await _client.PullRequest.Commits(user, repo, prNumber);
                shaCommitsToExclude.AddRange(commitsInPr.Select(x => x.Sha));

                // Collect files modified by commits
                var filesModifiedByPr = new List<string>();
                foreach (var commitInPr in commitsInPr)
                {
                    var commitOfPr = await _client.Repository.Commit.Get(user, repo, commitInPr.Sha);
                    filesModifiedByPr.AddRange(commitOfPr.Files.Select(x => x.Filename ?? x.PreviousFileName));
                }

                // bots can have their login as `dependabot[bot]`, so we turn it into `dependabot (bot)` to make it more Markdown compatible
                var login = pr.User.Login.Replace("[", " (").Replace(']', ')');
                changeLogCollection.AddPullRequestChange(prNumber, pr.Head.Label, pr.Title, pr.Body, login, pr.Labels.Select(x => x.Name).ToArray(), filesModifiedByPr.ToArray());
            }
            else
            {
                allCommits.Add(commit);
            }
        }

        // Add commit change
        var commitsNotInPR = allCommits.Where(x => !shaCommitsToExclude.Contains(x.Commit.Sha)).ToList();
        foreach (var commit in commitsNotInPR)
        {
            changeLogCollection.AddCommitChange(GetTitle(commit.Commit.Message), commit.Commit.Message, commit.Committer.Login, commit.Sha);
        }

        changeLogCollection.Version = new ChangelogVersionModel(Configuration.VersionPrefix, versionForCurrent, shaForCurrent);
        changeLogCollection.PreviousVersion = new ChangelogVersionModel(Configuration.VersionPrefix, versionForPrevious, shaForPrevious);

        changeLogCollection.CompareUrl = GetCompareUrl(user, repo, versionForPrevious?.OriginalVersion ?? shaForPrevious, versionForCurrent?.OriginalVersion ?? shaForCurrent);

        _log.Info($"Building Changelog: {changeLogCollection.CommitChanges.Count} commits and {changeLogCollection.PullRequestChanges.Count} PR collected for version {version}.{(versionForPrevious is not null ? $" Previous version is: {versionForPrevious.OriginalVersion}" : "")}");

        return changeLogCollection;
    }

    private static string GetTitle(string message) => new StringReader(message).ReadLine() ?? string.Empty;
    

    public async Task UploadRelease(string user, string repo, string versionTag, ChangelogResult? changelog, List<PackageEntry> entries)
    {
        var releases = await _client.Repository.Release.GetAll(user, repo);

        var versionOnGitHub = $"{Configuration.VersionPrefix}{versionTag}";

        Release? release = null;
        if (releases is not null)
        {
            release = releases.FirstOrDefault(releaseCheck => releaseCheck.TagName == versionOnGitHub);
        }

        // Create a release
        release ??= await _client.Repository.Release.Create(user, repo, new NewRelease(versionOnGitHub)
        {
            Name = changelog?.Title
        });

        _log.Info($"Loading release tag {release.TagName}");

        ReleaseUpdate? releaseUpdate = null;
        if (changelog is not null && release.Body != changelog.Body)
        {
            releaseUpdate = release.ToUpdate();
            releaseUpdate.Body = changelog.Body;
        }

        // Update the body if necessary
        if (releaseUpdate != null)
        {
            _log.Info($"Updating release {release.TagName} with new changelog");
            release = await _client.Repository.Release.Edit(user, repo, release.Id, releaseUpdate);
        }

        var assets = await _client.Repository.Release.GetAllAssets(user, repo, release.Id, ApiOptions.None);

        foreach (var entry in entries)
        {
            if (!entry.Publish)
            {
                _log.Info($"Skipping {entry.Path} as publishing is disabled.");
                break;
            }

            var filename = Path.GetFileName(entry.Path);
            if (assets.Any(x => x.Name == filename))
            {
                _log.Info($"No need to update {entry.Path} on GitHub. Already uploaded.");
                continue;
            }

            const int maxHttpRetry = 10;
            for (int i = 0; i < maxHttpRetry; i++)
            {
                try
                {
                    _log.Info($"{(i > 0 ? $"Retry ({{i}}/{maxHttpRetry - 1}) " : "")}Uploading {filename} to GitHub Release: {versionOnGitHub} (Size: {new FileInfo(entry.Path).Length / (1024 * 1024)}MB)");
                    // Upload assets
                    using var stream = File.OpenRead(entry.Path);

                    await _client.Repository.Release.UploadAsset(release, new ReleaseAssetUpload(filename, entry.Mime, stream, new TimeSpan(0, 5, 0)));
                    break;
                }
                catch (Exception ex)
                {
                    // In case of a failure to upload try to delete the asset
                    try
                    {
                        assets = await _client.Repository.Release.GetAllAssets(user, repo, release.Id, ApiOptions.None);
                        var assetToDelete = assets.FirstOrDefault(x => x.Name == filename);
                        if (assetToDelete != null)
                        {
                            await _client.Repository.Release.DeleteAsset(user, repo, assetToDelete.Id);
                        }

                        // Refresh the list of the assets
                        assets = await _client.Repository.Release.GetAllAssets(user, repo, release.Id, ApiOptions.None);
                    }
                    catch
                    {
                        _log.Error($"Failure to delete the remote asset: {filename}");
                        // ignored
                    }

                    if (i + 1 == maxHttpRetry)
                    {
                        _log.Error($"Upload failed: {ex} after {maxHttpRetry} retries.");
                    }
                    else
                    {
                        const int millisecondsDelay = 100;
                        _log.Warn($"Upload failed: {ex.Message}. Retrying after {millisecondsDelay}ms...");
                        await Task.Delay(millisecondsDelay);
                    }
                }
            }
        }
    }

    public async Task UploadHomebrewFormula(string user, string repo, PackageInfo packageInfo, string brewFormula)
    {
        var appName = packageInfo.ExeName;
        var filePath = $"Formula/{appName}.rb";

        Repository? existingRepository = null;
        try
        {
            existingRepository = await _client.Repository.Get(user, repo);
        }
        catch (NotFoundException)
        {
            // ignore
        }

        if (existingRepository is null)
        {
            _log.Info($"Creating Homebrew repository {user}/{repo}");
            var newRepository = new NewRepository(repo);
            newRepository.Description = $"Homebrew repository for {packageInfo.ProjectUrl}";
            newRepository.AutoInit = true;
            newRepository.LicenseTemplate = packageInfo.License;
            existingRepository = await _client.Repository.Create(newRepository);
        }
        else
        {
            _log.Info($"Homebrew repository found {user}/{repo}");
        }

        IReadOnlyList<RepositoryContent>? result = null;
        try
        {
            result = await _client.Repository.Content.GetAllContents(user, repo, filePath);
        }
        catch (NotFoundException)
        {
            // ignore
        }

        var shouldCreate = result is null || result.Count == 0;

        if (shouldCreate)
        {
            _log.Info($"Creating Homebrew Formula {user}/{repo}");
            await _client.Repository.Content.CreateFile(user, repo, filePath, new CreateFileRequest($"{packageInfo.Version}", brewFormula));
        }
        else
        {
            Debug.Assert(result is not null);
            var file = result[0];
            if (file.Content != brewFormula)
            {
                _log.Info($"Updating Homebrew Formula {user}/{repo}");
                await _client.Repository.Content.UpdateFile(user, repo, filePath, new UpdateFileRequest($"{packageInfo.Version}", brewFormula, file.Sha));
            }
            else
            {
                _log.Info($"No need to update Homebrew Formula {user}/{repo}. Already up-to-date.");
            }
        }

    }

    private static readonly Regex VersionRegex = new(@"^\d+(\.\d+)+");
    public string GetCompareUrl(string user, string repo, string fromRef, string toRef)
    {
        var fromVersionOrCommit = VersionRegex.IsMatch(fromRef) ? $"{Configuration.VersionPrefix}{fromRef}" : fromRef;
        var toVersionOrCommit = VersionRegex.IsMatch(toRef) ? $"{Configuration.VersionPrefix}{toRef}" : toRef;
        return $"{Configuration.Base}/{user}/{repo}/compare/{fromVersionOrCommit}...{toVersionOrCommit}";
    }

    public string GetDownloadReleaseUrl(string version, string fileEntry)
    {
        return $"{Configuration.Base}/releases/download/{Configuration.VersionPrefix}{version}/{Path.GetFileName(fileEntry)}";
    }
}