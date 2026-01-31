# AI Model Recommendations for Weather Analysis

## Best Models for Rain/Weather Detection

### 🥇 Recommended (Best Quality)

1. **GPT-4o (OpenAI)** via GitHub Models
   - **Tag**: `gpt-4o`
   - **Best for**: Overall image understanding, following complex instructions
   - **Rain detection**: Excellent at identifying water droplets on lens
   - **Speed**: Fast (1-3 seconds)
   - **Cost**: Free via GitHub Models (with limits)

2. **Claude 3.5 Sonnet (Anthropic)** via GitHub Models
   - **Tag**: `claude-3.5-sonnet`
   - **Best for**: Following detailed instructions precisely
   - **Rain detection**: Excellent, very good at spotting subtle details
   - **Speed**: Fast (1-3 seconds)
   - **Cost**: Free via GitHub Models (with limits)

### 🥈 Good Alternatives

3. **Gemini 1.5 Pro (Google)** via GitHub Models
   - **Tag**: `gemini-1.5-pro`
   - **Best for**: Complex scene understanding
   - **Rain detection**: Very good
   - **Speed**: Medium (2-4 seconds)
   - **Cost**: Free via GitHub Models (with limits)

4. **Gemini 1.5 Flash (Google)** via GitHub Models
   - **Tag**: `gemini-1.5-flash`
   - **Best for**: Fast analysis with good quality
   - **Rain detection**: Good
   - **Speed**: Very fast (0.5-2 seconds)
   - **Cost**: Free via GitHub Models (with limits)

### 🥉 Budget Options

5. **GPT-4o Mini (OpenAI)** via GitHub Models
   - **Tag**: `gpt-4o-mini`
   - **Best for**: Fast, cheap analysis
   - **Rain detection**: Good (but may miss subtle droplets)
   - **Speed**: Very fast (0.5-1 second)
   - **Cost**: Very low

## How to Use

### Via GitHub Models (Recommended - Free)
1. Go to Settings → AI Weather Monitor
2. Check "Use GitHub Models AI"
3. Select model: **GPT-4o** or **Claude 3.5 Sonnet**
4. Enter GitHub Personal Access Token
5. Get token from: https://github.com/settings/tokens
   - Create classic token with `read:user` scope
   - GitHub Models is free for development use

### Via Direct API Keys
- **OpenAI**: Enter OpenAI API key, uncheck GitHub Models
- **Anthropic**: Enter Anthropic API key
- **Google**: Enter Gemini API key

## Current Issue: Rain Not Detected

If the AI is not detecting rain when water droplets are clearly visible:

1. **Switch to GPT-4o or Claude 3.5 Sonnet** - these are best at following the detailed rain detection instructions
2. **Check your API key** - ensure it's valid and has credits
3. **Look at the logs** - check for API errors or analysis results
4. **Try forcing a new analysis** - click Refresh to capture a new image

## Testing Different Models

To compare models:
1. Save current image from `%TEMP%\AllSkyCameraPlugin\capture_*.jpg`
2. Switch model in settings
3. Click Refresh to analyze with new model
4. Compare results in Activity Log

## Model Performance Summary

| Model | Rain Detection | Speed | Cost | Best Use Case |
|-------|---------------|-------|------|---------------|
| GPT-4o | ⭐⭐⭐⭐⭐ | Fast | Free* | Best overall choice |
| Claude 3.5 | ⭐⭐⭐⭐⭐ | Fast | Free* | Best at following instructions |
| Gemini 1.5 Pro | ⭐⭐⭐⭐ | Medium | Free* | Complex scenes |
| Gemini 1.5 Flash | ⭐⭐⭐⭐ | Very Fast | Free* | Speed priority |
| GPT-4o Mini | ⭐⭐⭐ | Very Fast | Very Low | Budget option |

*Free via GitHub Models with rate limits
