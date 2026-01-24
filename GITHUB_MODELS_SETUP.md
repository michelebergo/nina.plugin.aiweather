# GitHub Models Setup Guide

This plugin uses GitHub Models to provide advanced AI-powered weather analysis using Claude, GPT-4, and Gemini models.

## What is GitHub Models?

GitHub Models provides free access to state-of-the-art AI models for development:
- **Claude 3.5 Sonnet** (Anthropic) - Excellent vision and analysis capabilities
- **GPT-4o / GPT-4o Mini** (OpenAI) - Strong reasoning and image understanding
- **Gemini 1.5 Flash/Pro** (Google) - Fast and accurate multimodal AI

## Getting Started

### 1. Create a GitHub Personal Access Token

1. Go to your GitHub account: [https://github.com/settings/tokens](https://github.com/settings/tokens)
2. Click **"Generate new token"** → **"Generate new token (classic)"**
3. Give it a descriptive name: e.g., "NINA All Sky Camera Plugin"
4. Set expiration (recommended: 90 days or No expiration for long-term use)
5. Select scopes - you only need minimal permissions for model access
6. Click **"Generate token"**
7. **IMPORTANT**: Copy the token immediately (you won't see it again!)

### 2. Configure the Plugin

1. Open NINA
2. Go to **Options → Plugins → All Sky Camera Weather Monitor**
3. Check **"Use GitHub Models AI"**
4. Select your preferred model:
   - **GPT-4o**: Best overall balance of speed and accuracy
   - **GPT-4o Mini**: Faster, more economical
   - **Claude 3.5 Sonnet**: Excellent for detailed image analysis
   - **Gemini 1.5 Flash**: Very fast responses
   - **Gemini 1.5 Pro**: Most capable Gemini model
5. Enter your GitHub token in the **"GitHub Token"** field
6. Click **Save**

### 3. Test the Connection

1. Go to **Equipment → Safety Monitor**
2. Select **"All Sky Camera Safety Monitor"**
3. Configure your RTSP stream URL
4. Click **Connect**
5. The plugin will capture a frame and analyze it using your selected AI model
6. Check **Help → Logs** for analysis results

## Model Comparison

| Model | Provider | Strengths | Speed | Best For |
|-------|----------|-----------|-------|----------|
| GPT-4o | OpenAI | Balanced, reliable | Fast | General use |
| GPT-4o Mini | OpenAI | Cost-effective | Very Fast | Frequent checks |
| Claude 3.5 Sonnet | Anthropic | Superior vision | Medium | Detailed analysis |
| Gemini 1.5 Flash | Google | Ultra-fast | Very Fast | Real-time monitoring |
| Gemini 1.5 Pro | Google | Most capable | Medium | Complex conditions |

## Rate Limits

GitHub Models offers generous free tier limits for development:
- **GPT-4o**: ~50 requests/minute
- **Claude 3.5**: ~50 requests/minute  
- **Gemini**: ~60 requests/minute

For typical usage (checking every 5 minutes), you'll stay well within limits.

## How It Works

When enabled, the plugin:

1. **Captures** a frame from your all-sky camera RTSP stream
2. **Encodes** the image as base64 JPEG
3. **Sends** to GitHub Models endpoint with specialized prompt
4. **AI analyzes** the image for:
   - Cloud coverage percentage
   - Weather conditions (clear, cloudy, rain, fog)
   - Safety assessment for imaging
   - Confidence level
5. **Returns** structured JSON response
6. **Updates** NINA safety status

## Sample AI Response

```json
{
  "condition": "PartlyCloudy",
  "cloudCoverage": 35,
  "rainDetected": false,
  "fogDetected": false,
  "isSafe": true,
  "description": "Partly cloudy night sky with scattered clouds covering approximately 35% of the visible sky. Stars visible between cloud gaps. No precipitation or fog detected. Conditions suitable for astronomical imaging.",
  "confidence": 92
}
```

## Troubleshooting

### "GitHub token not configured"
- Ensure you've entered a valid GitHub Personal Access Token
- Verify the token hasn't expired
- Try generating a new token

### "Failed to initialize GitHub Models service"
- Check your internet connection
- Verify the token has correct permissions
- Check NINA logs for detailed error messages

### "Falling back to local analysis"
- This happens when AI service fails
- Local image processing will be used instead
- Check token validity and network connection

### Rate limit errors
- Reduce check interval in plugin settings
- Consider using GPT-4o Mini for more frequent checks
- GitHub Models limits reset every minute

## Privacy & Security

- Your GitHub token is stored securely in NINA's settings
- Images are sent to GitHub's AI endpoint for analysis
- No images are permanently stored by GitHub
- Analysis happens in real-time and results are returned immediately
- You can switch back to Local AI anytime for offline operation

## Cost

**GitHub Models is FREE for development use!** 

There are no charges for using this plugin with GitHub Models during the preview period. GitHub may introduce paid tiers in the future, but the free tier is expected to remain generous.

## Advanced Configuration

### Custom System Prompt

The plugin uses a specialized prompt optimized for all-sky camera analysis. If you want to customize it, edit `GitHubModelsAnalysisService.cs`:

```csharp
new SystemChatMessage(@"You are an expert meteorologist analyzing all-sky camera images...")
```

### Model Endpoint

The plugin uses GitHub's official endpoint:
```
https://models.inference.ai.azure.com
```

This is compatible with Azure OpenAI SDK and provides access to all GitHub Models.

## Benefits Over Local AI

| Feature | Local AI | GitHub Models AI |
|---------|----------|------------------|
| Internet Required | ❌ No | ✅ Yes |
| Accuracy | Good | Excellent |
| Weather Description | Basic | Detailed |
| Rain Detection | Limited | Advanced |
| Fog Detection | Basic | Advanced |
| Confidence Scoring | Algorithmic | AI-based |
| Setup Complexity | None | Minimal |
| Cost | Free | Free (preview) |

## Recommendations

**For best results:**
- Start with **GPT-4o** - excellent balance
- If you need faster checks: use **GPT-4o Mini** or **Gemini Flash**
- For maximum accuracy: use **Claude 3.5 Sonnet**
- Check every 5-10 minutes for active monitoring
- Set cloud coverage threshold to 60-70% for conservative safety

**Test all models** with your specific camera setup to find which works best for your conditions!

---

**Questions?** Check the main [README.md](README.md) or open an issue on GitHub.
