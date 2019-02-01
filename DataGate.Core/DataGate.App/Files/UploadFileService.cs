﻿using DataGate.Com;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DataGate.App.Files
{
    /// <summary>上传文件处理器</summary>
    public class UploadFileService
    {
        SysFileMan _fileMan;
        /// <summary>构造函数</summary>
        public UploadFileService(SysFileMan fileMan, UploadConfig config)
        {
            _fileMan = fileMan;
            this._uploadDir = config.UploadFilesDir;
            this._uploadPath = config.UploadFilesPath;
            this._tempPath = config.UploadTempPath;
            ClearTempFiles();
        }

        /// <summary>上传目录</summary>
        string _uploadDir;

        /// <summary>上传路径</summary>
        string _uploadPath;

        /// <summary>上传临时路径</summary>
        private string _tempPath;

        /// <summary>处理文件上传</summary>
        public async Task<UploadResult> UploadAsync(ServerUploadRequest request)
        {
            UploadResult result = null;
            if (!request.Md5.IsEmpty())
            {
                result = await HandleClientMD5Async(request);
                if (!result.Id.IsEmpty())
                {
                    return result;
                }
            }
            if (request.Guid.IsEmpty())
            {
                result = await HandleSingleAsync(request);
            }
            else
            {
                result = await HandleChunkAsync(request);
            }
            return result;
        }

        /// <summary>处理秒传或无文件上传</summary>
        private async Task<UploadResult> HandleClientMD5Async(ServerUploadRequest request)
        {
            UploadResult result = new UploadResult();
            if (request.Md5.IsEmpty()) return result;
            var exists = await _fileMan.GetByMd5Async(request.Md5);
            if (exists != null)
            {
                result.Dup = true;

            }
            return result;
        }

        /// <summary>处理单文件上传</summary>
        private async Task<UploadResult> HandleSingleAsync(ServerUploadRequest request)
        {
            UploadResult result = new UploadResult();

            var doc = await BuildNewCheckExists(request, result);
            return result;
        }

        /// <summary>处理分片文件上传</summary>
        private async Task<UploadResult> HandleChunkAsync(ServerUploadRequest request)
        {
            UploadResult result = new UploadResult();

            if (request.Chunk < 0 || request.Chunks < 1 || request.Chunk > request.Chunks)
            {
                throw new Exception("分片参数无效！");
            }
            var uploadMD5 = request.Md5?.FirstOrDefault();
            result.Chunk = request.Chunk;
            if (request.Chunk < request.Chunks)
            {
                if (request.ServerFile.IsEmpty())
                {
                    throw new Exception("分片上传时流不存在！");
                }
                var chunkFile = GetNewChunkFile(request, request.Chunk);
                if (File.Exists(chunkFile))
                {
                    File.Delete(chunkFile);
                }
                File.Move(request.ServerFile, chunkFile);
            }
            else
            {
                #region 合并分片文件
                var mergeFile = GetNewMergeFile(request);
                List<string> chunkFiles = new List<string>();
                using (var mergeStream = new FileStream(mergeFile, FileMode.Create, FileAccess.Write))
                {
                    for (var i = 0; i < request.Chunks; i++)
                    {
                        var chunkFile = GetNewChunkFile(request, i);
                        using (var chunkStream = new FileStream(chunkFile, FileMode.Open, FileAccess.Read))
                        {
                            await chunkStream.CopyToAsync(mergeStream);
                        }
                        chunkFiles.Add(chunkFile);
                    }
                }
                request.ServerFile = mergeFile;
                var doc = await BuildNewCheckExists(request, result);
                result.Id = doc.Id;
                chunkFiles.ForEach(File.Delete);
                #endregion
            }
            return result;
        }

        /// <summary>生成新文档，如已存在则直接返回已存在文档 wang加</summary>
        private async Task<SysFile> BuildNewCheckExists(ServerUploadRequest request, UploadResult result)
        {
            var doc = BuildNew(request.RelativePath, request.FileName, request.CharSet);
            var uploadMD5 = request.Md5;
            doc.Md5 = BuildMD5(request.ServerFile, uploadMD5);
            var existsDoc = await _fileMan.GetByMd5Async(doc.Md5);
            if (existsDoc == null) //服务端去重， wang加
            {
                if (request.RelativePath.IsEmpty())
                {
                    var docFile = _uploadPath + doc.RelativePath;
                    File.Move(request.ServerFile, docFile);
                }
                await this._fileMan.InsertAsync(doc);
            }
            else
            {
                var docFile = _uploadPath + existsDoc.RelativePath;
                if (!File.Exists(docFile)) //万一找到的旧文件不存在，就复制新传的文件
                {
                    docFile = _uploadPath + doc.RelativePath;
                    File.Move(request.ServerFile, docFile);
                    await this._fileMan.UpdateManyAsync("Md5=@Md5", new { doc.RelativePath }, new { existsDoc.Md5 });
                }
                result.Dup = true;
                doc = existsDoc;
            }
            return doc;
        }

        private SysFile BuildNew(string relativePath, string fileName, string charSet)
        {
            var doc = new SysFile();
            doc.Id = Guid.NewGuid().ToString("N");
            doc.Name = fileName;
            doc.CreateTime = DateTime.Now;
            var ext = Path.GetExtension(fileName);
            if (relativePath.IsEmpty())
            {
                var levelOneDir = doc.CreateTime.ToString("yyyyMM");
                var levelTwoDir = doc.CreateTime.ToString("ddHH");
                var docPath = $@"{this._uploadPath}\{levelOneDir}\{levelTwoDir}";
                if (!Directory.Exists(docPath)) Directory.CreateDirectory(docPath);
                doc.RelativePath = $@"\{levelOneDir}\{levelTwoDir}\{doc.Id}{ext}";
            }
            else
            {
                doc.RelativePath = relativePath;
            }
            doc.ContentType = IOHelper.GetContentType(fileName);
            return doc;
        }

        /// <summary>校验MD5</summary>
        private void VerifyMD5(string serverFile, string uploadMD5)
        {
            if (uploadMD5.IsEmpty()) return;
            BuildMD5(serverFile, uploadMD5);
        }

        /// <summary>生成MD5</summary>
        private string BuildMD5(string serverFile, string uploadMD5)
        {
            //var buffer = File.ReadAllBytes(serverFile); //太占内存
            // var md5 = buffer.ToMD5().ToUpperInvariant();
            var md5 = IOHelper.GetMD5HashFromFile(serverFile).ToUpperInvariant();
            if (!uploadMD5.IsEmpty() && !md5.Equals(uploadMD5, StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception("上传文件MD5校验失败！");
            }
            return md5;
        }

        /// <summary>获得新分片文件路径</summary>
        private string GetNewChunkFile(ServerUploadRequest request, int chunk)
        {
            var ext = Path.GetExtension(request.FileName);
            return $@"{this._tempPath}\{request.Guid}_PART_{chunk.ToString()}{ext}";
        }

        /// <summary>获得分片合并文件路径</summary>
        private string GetNewMergeFile(ServerUploadRequest request)
        {
            var ext = Path.GetExtension(request.FileName);
            return $@"{this._tempPath}\{request.Guid}{ext}";
        }

        /// <summary>开始生成文档</summary>
        public SysFile BeginBuild(string fileName, string charSet = null)
        {
            return BuildNew(null, fileName, charSet);
        }

        /// <summary>根据FileID获得相关文件流, wang加</summary>
        public async Task<DownloadResult> DownloadAsync(string id)
        {
            var result = new DownloadResult();
            DownloadRequest request = new DownloadRequest();
            var file = await _fileMan.GetModelByIdAsync(id);
            if (file == null)
            {
                throw new Exception("指定文件不存在");
            }
            request.FileName = file.Name;
            request.ContentRef = file.RelativePath;
            return DownloadAsync(request, file);
        }

        DownloadResult DownloadAsync(DownloadRequest request, SysFile file)
        {
            var result = new DownloadResult();
            result.FileName = request.FileName; //wang加
            var relativePath = request.ContentRef.Replace('/', '\\');
            if (result.FileName.IsEmpty()) result.FileName = Path.GetFileName(relativePath);
            var filePath = this._uploadPath + relativePath;
            if (File.Exists(filePath))
                result.Content = File.OpenRead(filePath);
            else
                throw new Exception("指定文件不存在");
            result.ContentType = file.ContentType;
            return result;
        }



        /// <summary>获得上传文件夹下全部子文件夹</summary>
        public Task<List<UploadFolder>> GetUploadFoldersAsync()
        {
            var path = this._uploadPath;
            var folders = Directory.GetDirectories(path, "*", SearchOption.AllDirectories)
                .Select(e => new UploadFolder() { FullName = e.Substring(path.Length) })
                .ToList();
            for (int i = 0; i < folders.Count; i++)
            {
                var folder = folders[i];
                folder.Id = i + 1;
                folder.Name = folder.FullName.Substring(folder.FullName.LastIndexOf(Path.DirectorySeparatorChar) + 1);
                folder.Level = folder.FullName.Count(e => e == Path.DirectorySeparatorChar);
                folder.FullName = folder.FullName.Replace(Path.DirectorySeparatorChar, '/');
            }
            for (int i = 0; i < folders.Count; i++)
            {
                var folder = folders[i];
                if (folder.Level == 1) continue;
                folder.ParentId = folders.First(e => e.Level == folder.Level - 1 && folder.FullName.StartsWith(e.FullName, StringComparison.OrdinalIgnoreCase)).Id;
            }
            return Task.FromResult(folders);
        }
        /// <summary>获得某个上传文件夹下全部文件列表</summary>
        public Task<List<string>> GetUploadFolderFilesAsync(string folderFullName)
        {
            folderFullName = folderFullName.Replace('/', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);
            var path = $@"{this._uploadPath}\{folderFullName}";
            var files = Directory.GetFiles(path).Select(e => e.Substring(path.Length + 1)).ToList();
            return Task.FromResult(files);
        }
        /// <summary>清除临时文件夹中的过期文件</summary>
        public void ClearTempFiles()
        {
            var files = Directory.GetFiles(this._tempPath);
            var now = DateTime.Now;
            var delay = TimeSpan.FromHours(1);
            foreach (var file in files)
            {
                if ((now - File.GetCreationTime(file)) > delay)
                {
                    //删除超过一小时的
                    File.Delete(file);
                }
            }
        }
    }
}