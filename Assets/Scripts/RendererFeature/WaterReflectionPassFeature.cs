using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering.Universal;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class WaterReflectionPassFeature : ScriptableRendererFeature
{
    #region Fields
    [SerializeField] private Settings settings = new Settings();
    private RenderReflectionObjectPass _renderObjectPass = null;
    private MergeReflectionPass _mergeReflectionPass = null;
    #endregion

    // 設定
    [System.Serializable]
    public class Settings
    {
        // 水面の高さ (Y座標)
        public float waterY = 0f;

        // Skyboxをレンダリングする
        public bool renderSkybox = false;
        
        // レンダリング対象のレイヤーマスク
        public LayerMask cullingMask = -1;

        // レンダリングタイプ
        public RenderQueueType renderQueueType = RenderQueueType.Opaque;

        // 反射をレンダリングするタイミング
        public RenderPassEvent renderObjectPassEvent = RenderPassEvent.AfterRenderingOpaques; 
        
        // レンダリング結果をフレームバッファへ合成するタイミング (デバッグ用)
        public RenderPassEvent debugPassEvent = RenderPassEvent.AfterRenderingTransparents;

        // trueにすると、反射のデバッグ表示
        public bool debugReflection = false;
    }

    #region Defines
    // RenderTexture名の定義
    public static class RenderTextureNames
    {
        public static string _CameraReflectionTexture = "_CameraReflectionTexture";
    }
    
    // シェーダープロパティIDの定義
    public static class ShaderPropertyIDs
    {
        public static readonly int _CameraReflectionTexture = Shader.PropertyToID(RenderTextureNames._CameraReflectionTexture);
    }

    // RenderTargetIdentifierの定義
    public static class RenderTargetIdentifiers
    {
        public static readonly RenderTargetIdentifier _CameraReflectionTexture = ShaderPropertyIDs._CameraReflectionTexture;
    }
    
    // RTHandleの置き場所
    public static class RTHandlePool
    {
        public static RTHandle _CameraReflectionTexture;
    }
    #endregion
    
    #region RenderPass

    /// <summary>
    /// 反射をフレームバッファへ合成するパス (デバッグ用)
    /// </summary>
    class MergeReflectionPass : ScriptableRenderPass
    {
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var src = RenderTargetIdentifiers._CameraReflectionTexture;
            var dst = renderingData.cameraData.renderer.cameraColorTargetHandle;
            var cmd = CommandBufferPool.Get(nameof(MergeReflectionPass));
            cmd.Blit(src, dst);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }
    }
    
    /// <summary>
    /// 反射オブジェクトを描画するパス
    /// </summary>
    class RenderReflectionObjectPass : ScriptableRenderPass
    {
        private readonly string k_ProfilerTag = nameof(RenderReflectionObjectPass); // Frame Debugger で表示される名前
        
        // レンダリング対象のShaderTag
        private List<ShaderTagId> m_ShaderTagIdList = new List<ShaderTagId> 
        {
            new ShaderTagId("SRPDefaultUnlit"),
            new ShaderTagId("UniversalForward"),
            new ShaderTagId("UniversalForwardOnly"),
        };

        private WaterPlane _waterPlane = null;
        private FilteringSettings _filteringSettings;
        private RenderStateBlock _renderStateBlock;
        private LayerMask CullingMask => Settings.cullingMask;
        private RenderQueueType RenderQueueType => Settings.renderQueueType;

        public Settings Settings { get; set; }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            base.OnCameraSetup(cmd, ref renderingData);
            RTHandlePool._CameraReflectionTexture = RTHandles.Alloc(RenderTargetIdentifiers._CameraReflectionTexture);
        }
        
        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            base.Configure(cmd, cameraTextureDescriptor);
            
            // RenderTexture 確保 (使い終わったらReleaseTemporaryRTで解放)
            cmd.GetTemporaryRT(ShaderPropertyIDs._CameraReflectionTexture, cameraTextureDescriptor);
            
            // レンダリング先の変更
            ConfigureTarget(RTHandlePool._CameraReflectionTexture);
            
            // 描画クリア
            ConfigureClear(ClearFlag.All, Color.black);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            base.OnCameraCleanup(cmd);
            
            // 確保したRenderTextureを解放
            cmd.ReleaseTemporaryRT(ShaderPropertyIDs._CameraReflectionTexture);
            
            RTHandles.Release(RTHandlePool._CameraReflectionTexture);
        }
        
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (_waterPlane == null)
            {
                _waterPlane = WaterPlane.Instance;
            }

            if (_waterPlane != null)
            {
                Settings.waterY = _waterPlane.WaterY;
            }
            
            // レンダリング対象とするRenderQueue
            RenderQueueRange renderQueueRange = (RenderQueueType == RenderQueueType.Transparent)
                ? RenderQueueRange.transparent
                : RenderQueueRange.opaque;

            // フィルタリング設定
            _filteringSettings = new FilteringSettings(renderQueueRange, CullingMask);

            // オブジェクトのソート設定
            var sortingCriteria = (RenderQueueType == RenderQueueType.Transparent)
                ? SortingCriteria.CommonTransparent
                : renderingData.cameraData.defaultOpaqueSortFlags;

            // 描画 設定
            var drawingSettings = CreateDrawingSettings(
                m_ShaderTagIdList,
                ref renderingData,
                sortingCriteria);
            var cameraData = renderingData.cameraData;
            var defaultViewMatrix = cameraData.GetViewMatrix();
            var viewMatrix = cameraData.GetViewMatrix();
            
            // Y座標をwaterYだけ平行移動する行列
            var translateMat = Matrix4x4.identity;
            translateMat.m13 = -Settings.waterY; 
            
            // Y軸反転する行列
            var reverseMat = Matrix4x4.identity;
            reverseMat.m11 = -reverseMat.m11;
            
            var projectionMatrix = cameraData.GetProjectionMatrix();
            projectionMatrix =
                GL.GetGPUProjectionMatrix(projectionMatrix, cameraData.IsCameraProjectionMatrixFlipped());
            
            // コマンドバッファの確保 (使い終わったらCommandBufferPool.Releaseで解放する)
            var cmd = CommandBufferPool.Get(k_ProfilerTag);
            
            // 水面反転を行うように、View行列を加工する
            // 変換後の頂点座標 = P * V * Reverse * Translate * M * 頂点座標
            viewMatrix = viewMatrix * reverseMat * translateMat; 
            RenderingUtils.SetViewAndProjectionMatrices(cmd, viewMatrix, projectionMatrix, false);
            
            cmd.SetInvertCulling(true); // カリング反転 (ビュー行列を反転すると、メッシュの表・裏が逆転するため)
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            // レンダリング実行
            if (Settings.renderSkybox)
                context.DrawSkybox(renderingData.cameraData.camera);
            context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref _filteringSettings, ref _renderStateBlock);

            // 元に戻す
            cmd.SetInvertCulling(false);
            RenderingUtils.SetViewAndProjectionMatrices(cmd, defaultViewMatrix, projectionMatrix, false);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            
            // コマンドバッファ解放
            CommandBufferPool.Release(cmd);
        }
    }
    #endregion

    public override void Create()
    {
        RTHandles.Initialize(Screen.width, Screen.height);
        
        // Render Pass 作成
        _renderObjectPass = new RenderReflectionObjectPass();
        _renderObjectPass.Settings = settings;
        _renderObjectPass.renderPassEvent = settings.renderObjectPassEvent;
        
        _mergeReflectionPass = new MergeReflectionPass();
        _mergeReflectionPass.renderPassEvent = settings.debugPassEvent;

    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(_renderObjectPass);

        if (settings.debugReflection)
            renderer.EnqueuePass(_mergeReflectionPass);
    }
}